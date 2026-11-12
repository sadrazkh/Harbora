import PublicNote from './public-note';

// Rendered on the server for every request, never at build: SERVER_NOTE is not there during a build,
// and the time differs between one request and the next.
export const dynamic = 'force-dynamic';

export default function Home() {
  return (
    <main>
      <p id="server-note">{process.env.SERVER_NOTE || ''}</p>
      <p id="rendered-at">{Date.now()}</p>
      <PublicNote />
    </main>
  );
}
