'use client';

// A client component, so the value is written into the bundle a browser downloads, at build time.
export default function PublicNote() {
  return <p id="public-api">{process.env.NEXT_PUBLIC_API || ''}</p>;
}
