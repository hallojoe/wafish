export function WafishMark({ className = "size-6" }: { className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 96 96"
      fill="none"
      aria-hidden="true"
    >
      <path
        fill="currentColor"
        fillRule="evenodd"
        d="M30 46C40 31 58 27 72 33C84 38 91 49 90 60H46C50 70 67 75 83 71C76 82 58 86 41 79C23 72 19 56 19 43C9 39 5 30 6 20C19 19 27 27 28 36C31 24 41 19 51 22C48 34 42 40 32 42L30 46ZM80 45a3 3 0 1 0-6 0a3 3 0 1 0 6 0Z"
      />
      <path
        fill="currentColor"
        d="M52 62h4v6l-4-2ZM61 62h4v9l-4-1ZM70 62h4v10h-4ZM79 62h4v7l-4 2Z"
      />
    </svg>
  );
}
