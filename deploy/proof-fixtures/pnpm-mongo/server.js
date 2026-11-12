// Writes a document to the database it was given and reads it back, by the two names a project asks
// under: the one an attachment sets, and the one this project's own example file uses.
const http = require('http');
const { MongoClient } = require('mongodb');

async function roundTrip(uri) {
  if (!uri) return { ok: false, error: 'the variable is empty' };

  const client = new MongoClient(uri, { serverSelectionTimeoutMS: 5000 });
  try {
    await client.connect();
    const database = client.db();
    const notes = database.collection('proof_notes');
    const { insertedId } = await notes.insertOne({ said: 'hello', at: new Date() });
    const found = await notes.findOne({ _id: insertedId });
    return { ok: found !== null, said: found && found.said, database: database.databaseName };
  } catch (error) {
    return { ok: false, error: String(error.message || error) };
  } finally {
    await client.close().catch(() => {});
  }
}

http.createServer(async (req, res) => {
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({
    viaAttachName: await roundTrip(process.env.MONGODB_URI),
    viaExampleName: await roundTrip(process.env.MONGO_URL),
    node: process.version
  }));
}).listen(Number(process.env.PORT || 3000), '0.0.0.0', () => console.log('pnpm-mongo is listening'));
