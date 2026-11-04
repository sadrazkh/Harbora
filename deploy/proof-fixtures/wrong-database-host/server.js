// The commonest mistake there is: a connection hard-coded to the developer's own machine. It keeps
// serving, so nothing about the container looks wrong, and only its log says otherwise.
const http = require('http');
const { Client } = require('pg');

async function tryDatabase() {
  const client = new Client({ host: 'localhost', port: 5432, user: 'app', database: 'app', connectionTimeoutMillis: 3000 });
  try {
    await client.connect();
    await client.end();
    return true;
  } catch (error) {
    console.error('could not connect to the database:', error);
    return false;
  }
}

tryDatabase();
setInterval(tryDatabase, 5000);

http.createServer(async (req, res) => {
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({ database: await tryDatabase() }));
}).listen(Number(process.env.PORT || 3000), '0.0.0.0', () => console.log('wrong-database-host is listening'));
