// Asks the database it was given what it is, through the host, user, password and name a project
// usually reads. Then says what the MariaDB connector makes of DATABASE_URL as it stands, without
// ever repeating that address: the error the connector raises quotes it, password and all.
const http = require('http');
const mariadb = require('mariadb');

async function ask(config) {
  let connection;
  try {
    connection = await mariadb.createConnection(config);
    const rows = await connection.query('SELECT 1 AS one, VERSION() AS version, DATABASE() AS name');
    return { ok: true, one: Number(rows[0].one), version: rows[0].version, database: rows[0].name };
  } catch (error) {
    const said = String(error.message || error);
    return { ok: false, error: typeof config === 'string' ? said.split(config).join('<DATABASE_URL>').slice(0, 160) : said };
  } finally {
    if (connection) await connection.end().catch(() => {});
  }
}

http.createServer(async (req, res) => {
  const url = process.env.DATABASE_URL || '';
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({
    viaParts: await ask({
      host: process.env.DB_HOST,
      port: Number(process.env.DB_PORT || 3306),
      user: process.env.DB_USER,
      password: process.env.DB_PASSWORD,
      database: process.env.DB_NAME,
      connectTimeout: 5000
    }),
    dbHost: process.env.DB_HOST || null,
    urlScheme: url.includes('://') ? url.split('://')[0] : null,
    viaUrlAsGiven: url ? await ask(url) : { ok: false, error: 'DATABASE_URL is empty' }
  }));
}).listen(Number(process.env.PORT || 3000), '0.0.0.0', () => console.log('yarn-mariadb is listening'));
