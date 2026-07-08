import type { ButtonHTMLAttributes } from "react";

type Variant = "primary" | "secondary" | "ghost" | "dangerGhost";

const styles: Record<Variant, string> = {
  primary: "bg-acc text-accink font-bold hover:bg-acc2",
  secondary: "border border-border text-ink font-semibold hover:border-ink3",
  ghost: "text-ink2 font-semibold hover:bg-elev hover:text-ink",
  dangerGhost: "text-danger font-semibold hover:bg-danger/10",
};

export function Button({
  variant = "primary",
  className = "",
  loading = false,
  disabled,
  children,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; loading?: boolean }) {
  return (
    <button
      className={`inline-flex cursor-pointer items-center justify-center gap-1.5 rounded-lg px-4 py-2 text-sm transition-colors focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-offset-2 focus-visible:outline-teal disabled:opacity-50 ${styles[variant]} ${className}`}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      {...props}
    >
      {loading && (
        // reiner CSS-Spinner (aktuelle Textfarbe), keine Extra-Dependency
        <span
          className="inline-block size-3.5 shrink-0 animate-spin rounded-full border-2 border-current border-t-transparent"
          aria-hidden="true"
        />
      )}
      {children}
    </button>
  );
}
