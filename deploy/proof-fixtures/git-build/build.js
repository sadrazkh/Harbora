// A build that cannot go on without a variable, which is the commonest reason a first deploy fails
// and a second one, after somebody sets it, does not. It can also be told to take its time, so that
// something else can happen while it is running.
const fs = require('fs');

const api = process.env.NEXT_PUBLIC_API;
if (!api) {
  console.error('NEXT_PUBLIC_API is not set, and this build cannot go on without it.');
  process.exit(1);
}

const pause = Number(process.env.NEXT_PUBLIC_BUILD_PAUSE_SECONDS || 0);
setTimeout(() => fs.writeFileSync(`${__dirname}/built-api.txt`, api), pause * 1000);
