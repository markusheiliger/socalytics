export function getHeader(init: RequestInit, name: string): string | undefined {
  const headers = init.headers;
  if (!headers) return undefined;
  if (headers instanceof Headers) return headers.get(name) ?? undefined;
  if (Array.isArray(headers)) return headers.find(([key]) => key.toLowerCase() === name.toLowerCase())?.[1];
  const key = Object.keys(headers).find((k) => k.toLowerCase() === name.toLowerCase());
  return key ? (headers as Record<string, string>)[key] : undefined;
}

export function setHeader(init: RequestInit, name: string, value: string): void {
  if (init.headers instanceof Headers) {
    init.headers.set(name, value);
  } else if (Array.isArray(init.headers)) {
    init.headers.push([name, value]);
  } else {
    init.headers = { ...(init.headers ?? {}), [name]: value };
  }
}
