// Indexes a document in the search engine it was given and finds it again, through the names the
// engine's own client documents: a host that is a whole address, and a key.
import http from 'node:http';
import { Meilisearch } from 'meilisearch';

async function roundTrip() {
  try {
    const client = new Meilisearch({ host: process.env.MEILISEARCH_HOST, apiKey: process.env.MEILISEARCH_API_KEY });
    const index = client.index('proof_notes');
    const task = await index.addDocuments([{ id: 1, title: 'hello harbora' }]).waitTask({ timeout: 20000 });
    const found = await index.search('harbora');
    return {
      ok: task.status === 'succeeded' && found.hits.length === 1,
      task: task.status,
      hits: found.hits.length,
      title: found.hits[0]?.title ?? null
    };
  } catch (error) {
    return { ok: false, error: String(error.message || error) };
  }
}

http.createServer(async (req, res) => {
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({
    search: await roundTrip(),
    host: process.env.MEILISEARCH_HOST || null,
    // Its length, never its value: this answer is public.
    keyLength: (process.env.MEILISEARCH_API_KEY || '').length
  }));
}).listen(Number(process.env.PORT || 3000), '0.0.0.0', () => console.log('node-meilisearch is listening'));
