// One of two apps in one repository. It says which, so a launch of the other cannot pass for this one.
const http = require('http');

http.createServer((req, res) => {
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({ app: 'api' }));
}).listen(Number(process.env.PORT || 3000), '0.0.0.0', () => console.log('api is listening'));
