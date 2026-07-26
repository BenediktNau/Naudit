import type { InputHTMLAttributes, SelectHTMLAttributes, TextareaHTMLAttributes } from "react";

/** Gemeinsame Feld-Optik: dunkler als die Karte, Fokus zieht den Akzentring.
 *  Vorher lag dieser Klassen-String in acht Dateien als Kopie. */
export const fieldCls =
  "rounded-[10px] border border-border bg-input px-3 py-2.5 text-[12.5px] text-ink outline-none " +
  "placeholder:text-ink3 transition-[border-color,box-shadow] duration-200 " +
  "focus:border-acc focus:shadow-[0_0_0_3px_rgba(74,222,128,.1)] " +
  "disabled:cursor-not-allowed disabled:text-ink3";

export function Input({ className = "", ...props }: InputHTMLAttributes<HTMLInputElement>) {
  return <input className={`${fieldCls} ${className}`} {...props} />;
}

export function Textarea({ className = "", ...props }: TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea className={`${fieldCls} font-mono leading-relaxed ${className}`} {...props} />;
}

export function Select({ className = "", children, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <select className={`${fieldCls} cursor-pointer py-2 ${className}`} {...props}>
      {children}
    </select>
  );
}
