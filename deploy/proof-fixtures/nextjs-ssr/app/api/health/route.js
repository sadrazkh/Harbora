export const dynamic = 'force-dynamic';

export async function GET() {
  return Response.json({ ok: true, node: process.version, answeredAt: Date.now() });
}
