export function WafishMark({ className = "size-6" }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 96 96" fill="none" aria-hidden="true">
      <path
        d="M14 20v32l18 24 16-24 16 24 18-24V20"
        stroke="currentColor"
        strokeWidth="10"
        strokeLinecap="square"
        strokeLinejoin="round"
      />
      <path d="M48 22v30" stroke="currentColor" strokeWidth="10" />
      <path d="M41 15h14v14H41z" fill="currentColor" />
    </svg>
  );
}
