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
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant }) {
  return (
    <button
      className={`cursor-pointer rounded-lg px-4 py-2 text-sm transition-colors focus-visible:outline-2 focus-visible:outline-teal disabled:opacity-50 ${styles[variant]} ${className}`}
      {...props}
    />
  );
}
