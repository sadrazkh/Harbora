// A build that succeeds and produces no site: a compiled backend with no start script looks like this.
const fs = require('fs');

fs.mkdirSync('dist', { recursive: true });
fs.writeFileSync('dist/server.js', 'console.log("not a site");\n');
