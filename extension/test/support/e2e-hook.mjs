// Resolve hook for the node tests: the extension imports the shared crypto as ../js/e2e.js, which only exists in a build
// (build.mjs copies it there). The tests map it to the one copy in the repo instead of forking it.
const E2E = new URL('../../../src/PrivatePaste.Server/wwwroot/js/e2e.js', import.meta.url).href;

export async function resolve(specifier, context, next) {
  if (specifier.endsWith('/js/e2e.js') && context.parentURL?.includes('/extension/src/'))
    return { url: E2E, shortCircuit: true };
  return next(specifier, context);
}
