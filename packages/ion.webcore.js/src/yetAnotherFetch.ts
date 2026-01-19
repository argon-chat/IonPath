export interface FetchResult {
  status: number | "network-error";
  buffer: ArrayBuffer | null;
}

export async function safeFetchBuffer(
  url: string,
  init?: RequestInit
): Promise<FetchResult> {
  try {
    const resp = await fetch(url, init);
    try {
      const buffer = await resp.arrayBuffer();
      return { status: resp.status, buffer };
    } catch {
      return { status: resp.status, buffer: null };
    }
  } catch {
    return { status: "network-error", buffer: null };
  }
}