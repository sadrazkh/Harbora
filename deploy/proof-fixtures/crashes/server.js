// Answers for a few seconds and then dies, every time. A deployment's probe sees a healthy app; what
// happens next is what the checks are for.
const http = require('http');

http.createServer((req, res) => res.end('alive for now\n'))
  .listen(Number(process.env.PORT || 3000), '0.0.0.0', () => console.log('crashes is listening'));

setTimeout(() => {
  console.error('crashes: giving up on purpose');
  process.exit(3);
}, 8000);
