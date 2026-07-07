import { useUsage, fmtTokens } from "@/hooks/queries";
import { useAuth } from "@/lib/auth";
import { Panel } from "@/components/ui/Panel";
import { Pill } from "@/components/ui/Pill";

export function ProfilePage() {
  const { me } = useAuth();
  const { data, isLoading } = useUsage();
  if (isLoading || !data) return <div className="p-8 font-mono text-ink3">loading…</div>;

  const maxMonth = Math.max(...data.monthly.map((m) => m.tokens), 1);
  const maxProject = Math.max(...data.perProject.map((p) => p.tokens), 1);
  const total = data.monthly.reduce((s, m) => s + m.tokens, 0);

  return (
    <div className="flex flex-col gap-5 px-7 py-6">
      <div className="flex items-center gap-5">
        <span className="grid size-16 place-items-center rounded-full border border-border bg-elev font-mono text-2xl font-bold text-acc">
          {me.username?.slice(0, 1).toUpperCase()}
        </span>
        <div className="flex-1">
          <div className="font-mono text-xl font-bold">{me.username}</div>
          {me.isAdmin && <div className="mt-1 font-mono text-[11px] tracking-widest text-teal uppercase">admin</div>}
        </div>
        <Pill kind="ok">✓ active</Pill>
      </div>

      <div className="grid grid-cols-1 items-start gap-4 lg:grid-cols-[2fr_1fr]">
        <Panel title="Tokens · last 6 months" extra={`${fmtTokens(total)} total`}>
          <div className="flex h-[150px] items-end gap-3.5 px-5 pt-4 pb-3">
            {data.monthly.map((m, i) => {
              const last = i === data.monthly.length - 1;
              return (
                <div key={m.month} className="flex flex-1 flex-col items-center gap-2">
                  <div
                    className={`w-full rounded-t ${last ? "bg-acc" : i === data.monthly.length - 2 ? "bg-acc/35" : "bg-elev"}`}
                    style={{ height: `${Math.max(4, (m.tokens / maxMonth) * 100)}px` }}
                    title={`${m.month}: ${fmtTokens(m.tokens)}`}
                  />
                  <span className={`font-mono text-[10px] ${last ? "text-acc" : "text-ink3"}`}>
                    {new Date(`${m.month}-01`).toLocaleDateString("en", { month: "short" })}
                  </span>
                </div>
              );
            })}
          </div>
        </Panel>
        <div className="flex flex-col gap-4">
          <Panel title="Reviews total">
            <div className="px-5 py-4 font-mono text-3xl font-bold tabular-nums">{data.reviewsTotal}</div>
          </Panel>
          <Panel title="Avg tokens / review">
            <div className="px-5 py-4 font-mono text-3xl font-bold tabular-nums">{fmtTokens(data.avgTokens)}</div>
          </Panel>
        </div>
      </div>

      <Panel title={`Per project · ${new Date().toLocaleDateString("en", { month: "long" })}`}>
        {data.perProject.length === 0 && <div className="px-5 py-5 font-mono text-xs text-ink3">No usage this month.</div>}
        {data.perProject.map((p) => (
          <div key={p.name} className="flex items-center gap-4 border-b border-hairline px-5 py-3.5 last:border-b-0">
            <span className="w-[160px] shrink-0 truncate font-mono text-[13px]">{p.name}</span>
            <div className="h-2 flex-1 overflow-hidden rounded-full bg-elev">
              <div className="h-full bg-acc" style={{ width: `${(p.tokens / maxProject) * 100}%` }} />
            </div>
            <span className="w-14 text-right font-mono text-xs text-ink2 tabular-nums">{fmtTokens(p.tokens)}</span>
          </div>
        ))}
      </Panel>
    </div>
  );
}
