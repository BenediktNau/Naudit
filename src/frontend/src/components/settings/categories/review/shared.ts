import { fieldCls } from "@/components/ui/Input";

/** Einheitliche Feldbreite/-optik der Review-Panels (Select, Zahl- und Textfeld).
 *  Baut auf dem gemeinsamen Feld-Token auf, damit diese Panels denselben Fokusring
 *  und dieselbe Fläche haben wie jedes andere Eingabefeld der Oberfläche. */
export const selCls = `${fieldCls} w-[220px] font-mono text-[12px] disabled:opacity-50`;
