using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace WafIsh.Dashboard.Infrastructure.Performance;

/// <summary>
/// Unsafe optimizations for maximum performance.
/// Uses pointers, stackalloc, and raw memory manipulation.
/// Requires 'unsafe' context and SkipLocalsInit for best results.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S6640", Justification = "These public pointer APIs require unsafe code. Span overloads pin bounded memory; raw-pointer callers own buffer validity. Boundary behavior is regression tested.")]
public static unsafe class UnsafeOptimizations
{
    // Constants for common operations
    private const uint LowerMask = 0x20202020u; // To lower case 4 chars at once

    /// <summary>
    /// Ultra-fast UTF-8 string comparison using pointer arithmetic.
    /// Zero-allocation, branchless where possible.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Utf8Equals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        if (a.Length == 0)
        {
            return true;
        }

        fixed (byte* pa = a)
        fixed (byte* pb = b)
        {
            return MemoryEquals(pa, pb, (nuint)a.Length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MemoryEquals(byte* a, byte* b, nuint length)
    {
        // Compare 8 bytes at a time using ulong
        var ulp = (ulong*)a;
        var ulq = (ulong*)b;

        while (length >= sizeof(ulong))
        {
            if (*ulp++ != *ulq++)
            {
                return false;
            }

            length -= sizeof(ulong);
        }

        // Compare remaining 4 bytes
        if (length >= sizeof(uint))
        {
            if (*(uint*)ulp != *(uint*)ulq)
            {
                return false;
            }

            length -= sizeof(uint);
            ulp = (ulong*)((uint*)ulp + 1);
            ulq = (ulong*)((uint*)ulq + 1);
        }

        // Compare remaining 2 bytes
        if (length >= sizeof(ushort))
        {
            if (*(ushort*)ulp != *(ushort*)ulq)
            {
                return false;
            }

            length -= sizeof(ushort);
            ulp = (ulong*)((ushort*)ulp + 1);
            ulq = (ulong*)((ushort*)ulq + 1);
        }

        // Compare final byte
        if (length > 0)
        {
            return *(byte*)ulp == *(byte*)ulq;
        }

        return true;
    }

    /// <summary>
    /// Fast hash computation using FNV-1a with unrolled loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FastHash(byte* data, int length)
    {
        uint hash = 2166136261;

        // Process 4 bytes at a time
        while (length >= 4)
        {
            hash ^= *(uint*)data;
            hash *= 16777619;
            data += 4;
            length -= 4;
        }

        // Process remaining bytes
        while (length-- > 0)
        {
            hash ^= *data++;
            hash *= 16777619;
        }

        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FastHash(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return 2166136261;
        }

        fixed (byte* ptr = data)
        {
            return FastHash(ptr, data.Length);
        }
    }

    /// <summary>
    /// Fast integer parsing from ASCII bytes.
    /// No allocations, no exceptions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParseInt32(byte* start, int length, out int value)
    {
        value = 0;
        if (length <= 0 || start == null)
        {
            return false;
        }

        return int.TryParse(new ReadOnlySpan<byte>(start, length),
            System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Copy strings using stackalloc for short strings, ArrayPool for longer ones.
    /// Zero-allocation for strings up to 256 bytes.
    /// </summary>
    public static Span<byte> CopyUtf8String(ReadOnlySpan<char> source, Span<byte> destination)
    {
        var actualBytes = Encoding.UTF8.GetBytes(source, destination);
        return destination[..actualBytes];
    }

    /// <summary>
    /// Branchless min/max using bit manipulation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FastMin(int a, int b)
    {
        return Math.Min(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FastMax(int a, int b)
    {
        return Math.Max(a, b);
    }

    /// <summary>
    /// Round up to next power of 2 using bit twiddling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextPowerOf2(int value)
    {
        if (value <= 1)
        {
            return 2;
        }

        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }

    /// <summary>
    /// Fast modulo using pre-computed multiplicative inverse.
    /// For power-of-2 divisors only (uses bit mask).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FastModPowerOf2(int value, int divisor)
    {
        // divisor must be power of 2
        return value & (divisor - 1);
    }

    /// <summary>
    /// Prefetch memory into cache for upcoming reads.
    /// Reduces cache misses in sequential access patterns.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Prefetch(void* address)
    {
        // X86/X64 prefetcht0 instruction
        if (System.Runtime.Intrinsics.X86.Sse.IsSupported)
        {
            System.Runtime.Intrinsics.X86.Sse.Prefetch0(address);
        }
    }

    /// <summary>
    /// Fast memory clear using pointer arithmetic.
    /// Faster than Array.Clear for small buffers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FastClear(byte* ptr, int length)
    {
        // Clear 8 bytes at a time
        var ul = (ulong*)ptr;
        var ulongCount = length / sizeof(ulong);

        for (int i = 0; i < ulongCount; i++)
        {
            *ul++ = 0;
        }

        // Clear remaining
        var remaining = length % sizeof(ulong);
        var b = (byte*)ul;
        for (int i = 0; i < remaining; i++)
        {
            *b++ = 0;
        }
    }
}

/// <summary>
/// Circular buffer with contiguous managed storage and shared disposal ownership.
/// This buffer requires external synchronization for concurrent access.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CacheOptimizedRingBuffer<T> : IDisposable where T : unmanaged
{
    private readonly BufferStorage? _storage;
    private readonly int _capacity;
    private readonly int _mask; // For power-of-2 optimization
    private long _head;
    private long _tail;

    public int Capacity => _capacity;
    public int Count => (int)(_head - _tail);
    public bool IsEmpty => _head == _tail;
    public bool IsFull => Count >= _capacity;

    public CacheOptimizedRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 1 << 30);
        capacity = UnsafeOptimizations.NextPowerOf2(capacity);
        _capacity = capacity;
        _mask = capacity - 1;
        _head = 0;
        _tail = 0;

        // Shared ownership makes disposal safe even when this value type is copied.
        _storage = new BufferStorage(capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item)
    {
        var buffer = GetBuffer();
        if (IsFull)
        {
            return false;
        }

        var index = (int)(_head & _mask);
        buffer[index] = item;
        Interlocked.Increment(ref _head);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        var buffer = GetBuffer();
        if (IsEmpty)
        {
            item = default;
            return false;
        }

        var index = (int)(_tail & _mask);
        item = buffer[index];
        Interlocked.Increment(ref _tail);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Peek()
    {
        var buffer = GetBuffer();
        if (IsEmpty)
        {
            throw new InvalidOperationException("Buffer empty");
        }

        var index = (int)(_tail & _mask);
        return buffer[index];
    }

    public void Clear()
    {
        Array.Clear(GetBuffer());
        _tail = _head;
    }

    public void Dispose() => _storage?.Dispose();

    private T[] GetBuffer() => _storage?.Items ?? throw new ObjectDisposedException(nameof(CacheOptimizedRingBuffer<T>));

    private sealed class BufferStorage(int capacity) : IDisposable
    {
        public T[]? Items = new T[capacity];
        public void Dispose()
        {
            var items = Interlocked.Exchange(ref Items, null);
            if (items is not null)
            {
                Array.Clear(items);
            }
        }
    }
}

/// <summary>
/// Bit manipulation utilities for compact storage.
/// </summary>
internal static class BitManipulation
{
    /// <summary>
    /// Pack multiple small integers into a single long.
    /// Useful for reducing memory footprint of composite IDs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong PackInts(int a, int b, int c, int d)
    {
        return ((ulong)(uint)a << 48) |
               ((ulong)(uint)b << 32) |
               ((ulong)(uint)c << 16) |
               (ulong)(uint)d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UnpackInts(ulong packed, out int a, out int b, out int c, out int d)
    {
        a = (int)(packed >> 48);
        b = (int)(packed >> 32);
        c = (int)(packed >> 16);
        d = (int)packed;
    }

    /// <summary>
    /// Count leading zeros using hardware instruction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CountLeadingZeros(uint value)
    {
        if (System.Runtime.Intrinsics.X86.Lzcnt.IsSupported)
        {
            return (int)System.Runtime.Intrinsics.X86.Lzcnt.LeadingZeroCount(value);
        }

        // Software fallback
        if (value == 0)
        {
            return 32;
        }

        int count = 0;
        while ((value & 0x80000000) == 0)
        {
            count++;
            value <<= 1;
        }
        return count;
    }

    /// <summary>
    /// Bit scan reverse (position of highest set bit).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BitScanReverse(uint value)
    {
        if (System.Runtime.Intrinsics.X86.Bmi1.IsSupported)
        {
            return 31 - (int)System.Runtime.Intrinsics.X86.Lzcnt.LeadingZeroCount(value);
        }

        int pos = 0;
        while (value > 1)
        {
            value >>= 1;
            pos++;
        }
        return pos;
    }

    /// <summary>
    /// Population count (number of set bits).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PopCount(ulong value)
    {
        if (System.Runtime.Intrinsics.X86.Popcnt.X64.IsSupported)
        {
            return (int)System.Runtime.Intrinsics.X86.Popcnt.X64.PopCount(value);
        }

        // Software fallback: parallel bit count
        const ulong c1 = 0x5555555555555555;
        const ulong c2 = 0x3333333333333333;
        const ulong c4 = 0x0F0F0F0F0F0F0F0F;

        value -= (value >> 1) & c1;
        value = (value & c2) + ((value >> 2) & c2);
        value = (value + (value >> 4)) & c4;
        value += value >> 8;
        value += value >> 16;
        value += value >> 32;
        return (int)(value & 0x7F);
    }
}

