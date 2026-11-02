// Builds the unpacked extension: node extension/build.mjs [--version X.Y.Z] [--fake-clipboard] [--out <dir>]
//
// No bundler: src/ is copied as is, and the crypto (e2e.js and its Argon2 library) and the Sora font are copied from
// the web UI so there is only ever one copy of them in the repo. --fake-clipboard swaps the real clipboard for an
// in-memory one that the automated end-to-end tests drive, so they never touch the OS clipboard.

import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const wwwroot = join(here, '..', 'src', 'PrivatePaste.Server', 'wwwroot');

function parseArgs(argv) {
  const args = { version: '0.0.0', fakeClipboard: false, out: join(here, 'dist') };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--fake-clipboard') args.fakeClipboard = true;
    else if (a === '--version') args.version = argv[++i];
    else if (a === '--out') args.out = resolve(argv[++i]);
    else throw new Error(`Unknown argument: ${a}`);
  }
  return args;
}

// Edge wants 1 to 4 dot-separated numbers in `version`; a prerelease suffix (0.6.0-rc1) goes to `version_name`,
// the same way the MSI drops it.
function manifestVersion(tag) {
  const m = /^(\d+(?:\.\d+){0,3})(-[0-9A-Za-z.-]+)?$/.exec(tag ?? '');
  if (!m) throw new Error(`Not a version Edge accepts: ${tag}`);
  return m[2] ? { version: m[1], version_name: tag } : { version: m[1] };
}

const args = parseArgs(process.argv.slice(2));
const versions = manifestVersion(args.version);

// Only ever wipe an earlier build or an empty folder, never a directory passed by mistake.
if (existsSync(args.out) && readdirSync(args.out).length && !existsSync(join(args.out, 'manifest.json')))
  throw new Error(`${args.out} is not empty and holds no earlier build; pick another --out`);
rmSync(args.out, { recursive: true, force: true });
mkdirSync(args.out, { recursive: true });

cpSync(join(here, 'src'), args.out, {
  recursive: true,
  filter: src => !src.endsWith(join('platform', 'fake-clipboard.js')),
});
cpSync(join(wwwroot, 'js', 'e2e.js'), join(args.out, 'js', 'e2e.js'));
cpSync(join(wwwroot, 'lib', 'hash-wasm'), join(args.out, 'lib', 'hash-wasm'), { recursive: true });
// The popup's Sora font (popup.css loads fonts/*.woff2), from the web UI like the crypto, with its licence.
cpSync(join(wwwroot, 'fonts'), join(args.out, 'fonts'), { recursive: true });

if (args.fakeClipboard)
  cpSync(join(here, 'src', 'platform', 'fake-clipboard.js'), join(args.out, 'platform', 'clipboard.js'));

const manifest = { ...JSON.parse(readFileSync(join(here, 'manifest.template.json'), 'utf8')), ...versions };
writeFileSync(join(args.out, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');

console.log(`Built ${manifest.version_name ?? manifest.version}${args.fakeClipboard ? ' (fake clipboard)' : ''} into ${args.out}`);
