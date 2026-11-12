// Says which commit it was built from. The proof writes a different marker on each branch before it
// pushes, so the answer is the branch that was really cloned.
const fs = require('fs');
const http = require('http');
const path = require('path');

const marker = fs.readFileSync(path.join(__dirname, 'marker.txt'), 'utf8').trim();

http.createServer((req, res) => {
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({ marker }));
}).listen(Number(process.env.PORT || 3000), '0.0.0.0', () => console.log('git-node is listening'));
