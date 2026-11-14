using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;

var directory = Path.GetFullPath(args[0]);
var baselineDirectory = Path.GetFullPath(args[1]);
var update = args.Contains("--update");
var files = Directory.GetFiles(directory, "Wafish*.dll");
var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
var references = platform.Concat(files).Distinct().Select(p => MetadataReference.CreateFromFile(p)).ToArray();
var compilation = CSharpCompilation.Create("ApiCheck", references: references);
var format = new SymbolDisplayFormat(
    globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
    memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeConstantValue | SymbolDisplayMemberOptions.IncludeExplicitInterface,
    parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut,
    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
var failures = new List<string>();
Directory.CreateDirectory(baselineDirectory);
foreach (var file in files)
{
    var reference = references.Single(r => r.FilePath == file);
    var assembly = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference)!;
    var surface = new SortedSet<string>(StringComparer.Ordinal);
    void Visit(ISymbol symbol)
    {
        if (symbol is INamespaceSymbol ns) { foreach (var child in ns.GetMembers()) Visit(child); return; }
        if (symbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)) return;
        surface.Add(symbol.Kind + ": " + symbol.ToDisplayString(format));
        if (symbol is INamedTypeSymbol type)
        {
            if (type.BaseType != null) surface.Add("Base: " + type.ToDisplayString(format) + " : " + type.BaseType.ToDisplayString(format));
            foreach (var contract in type.Interfaces) surface.Add("Interface: " + type.ToDisplayString(format) + " : " + contract.ToDisplayString(format));
            foreach (var child in type.GetMembers()) Visit(child);
        }
    }
    Visit(assembly.GlobalNamespace);
    var baseline = Path.Combine(baselineDirectory, assembly.Name + ".json");
    if (update) File.WriteAllText(baseline, JsonSerializer.Serialize(surface, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    else if (!File.Exists(baseline)) failures.Add("Missing API baseline: " + assembly.Name);
    else foreach (var missing in JsonSerializer.Deserialize<string[]>(File.ReadAllText(baseline))!.Except(surface)) failures.Add(assembly.Name + ": removed or changed " + missing);
    Console.WriteLine($"API: {assembly.Name}, {surface.Count} public declarations");
}
if (!update)
{
    var names = files.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
    foreach (var baseline in Directory.GetFiles(baselineDirectory, "*.json"))
        if (!names.Contains(Path.GetFileNameWithoutExtension(baseline))) failures.Add("Missing package assembly: " + Path.GetFileNameWithoutExtension(baseline));
}
if (files.Length == 0) failures.Add("No package assemblies were supplied.");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;
