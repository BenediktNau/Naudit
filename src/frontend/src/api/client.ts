export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

/** Zentraler fetch-Wrapper: JSON rein/raus, Fehler als ApiError mit Status.
 *  401 wird vom AuthGate (lib/auth.tsx) global behandelt. */
export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    headers: init?.body ? { "Content-Type": "application/json" } : undefined,
    ...init,
  });
  if (!res.ok) {
    throw new ApiError(res.status, `${init?.method ?? "GET"} ${path} failed: HTTP ${res.status}`);
  }
  // Leerer Body (204, aber auch ein leeres 200) ⇒ kein JSON.parse — sonst wirft der Client bei
  // Aktions-Endpunkten ohne Rückgabe („approve"/„reject"/„logout") trotz Erfolg (HTTP 2xx).
  const text = await res.text();
  return (text ? (JSON.parse(text) as T) : (undefined as T));
}
