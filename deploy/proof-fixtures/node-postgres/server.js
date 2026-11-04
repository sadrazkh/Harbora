// Answers with what it was really able to do with what it was given. It reaches its database twice,
// by the two ways a project usually asks: one URL, or a host with the driver's own PG* variables.
const fs = require('fs');
const http = require('http');
const path = require('path');
const { Client } = require('pg');

const version = fs.readFileSync(path.join(__dirname, 'version.txt'), 'utf8').trim();

async function selectOne(config) {
  const client = new Client({ ...config, connectionTimeoutMillis: 5000 });
  try {
    await client.connect();
    const result = await client.query('SELECT 1 AS one');
    return { ok: true, one: result.rows[0].one };
  } catch (error) {
    return { ok: false, error: String(error.message || error) };
  } finally {
    await client.end().catch(() => {});
  }
}

const server = http.createServer(async (req, res) => {
  const viaUrl = await selectOne({ connectionString: process.env.DATABASE_URL });
  const viaHost = await selectOne({
    host: process.env.DB_HOST,
    user: process.env.PGUSER,
    password: process.env.PGPASSWORD,
    database: process.env.PGDATABASE
  });
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({
    version,
    viaUrl,
    viaHost,
    dbHost: process.env.DB_HOST || null,
    // Its length, never its value: this answer is public.
    jwtSecretLength: (process.env.JWT_SECRET || '').length,
    apiKey: process.env.API_KEY || null,
    logLevel: process.env.LOG_LEVEL || null
  }));
});

server.listen(Number(process.env.PORT || 3000), '0.0.0.0', () => console.log('node-postgres is listening'));
