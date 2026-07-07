import type { MeDto } from "@/api/types";
import { Button } from "@/components/ui/Button";
import { Logo } from "@/components/ui/Logo";
import { Pill } from "@/components/ui/Pill";

/** Eingeloggt, aber (noch) nicht freigeschaltet — pending oder rejected. */
export function PendingPage({ me, onLogout }: { me: MeDto; onLogout: () => Promise<void> }) {
  const rejected = me.status === "Rejected";
  return (
    <div className="grid min-h-full place-items-center p-8">
      <div className="flex w-[420px] max-w-full flex-col items-center text-center">
        <Logo size={56} />
        <div className="mt-5 font-mono text-lg font-bold">{me.username}</div>
        <div className="mt-3">
          {rejected ? <Pill kind="danger">✕ access revoked</Pill> : <Pill kind="warn">● pending approval</Pill>}
        </div>
        <p className="mt-5 text-sm leading-relaxed text-ink2">
          {rejected
            ? "Your access has been revoked. Contact the administrator if you think this is a mistake."
            : "Your account is waiting for an admin to approve it. Reviews for your repositories start running once you are approved."}
        </p>
        <Button variant="secondary" className="mt-7" onClick={() => void onLogout()}>
          Sign out
        </Button>
      </div>
    </div>
  );
}
