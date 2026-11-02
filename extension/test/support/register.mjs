// Import first, then load extension modules with a dynamic import() so they resolve through the hook.
import { register } from 'node:module';

register('./e2e-hook.mjs', import.meta.url);
