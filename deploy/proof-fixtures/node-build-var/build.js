// What a framework does with NEXT_PUBLIC_*: reads it during the build and writes it into the output.
require('fs').writeFileSync(`${__dirname}/built-api.txt`, process.env.NEXT_PUBLIC_API || '');
