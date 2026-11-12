const fs = require('fs');
const http = require('http');

const builtWith = fs.readFileSync(`${__dirname}/built-api.txt`, 'utf8');

http.createServer((req, res) => {
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({ builtWith }));
}).listen(Number(process.env.PORT || 3000), '0.0.0.0', () => console.log('git-build is listening'));
