// The popup's one way to talk to the engine (messages.js, in the offscreen document). Only "nobody is listening yet"
// is retried: the offscreen document is still starting. Anything else (a port closed mid-request, a reply too big to
// send) fails at once, so a sign-in, unlock or download is never sent twice.

export const NOT_RUNNING = "PrivatePaste isn't running yet. Try again in a moment.";
export const FAILED = 'Something went wrong. Try again.';

/** An answer to show: the engine's error text, or one of the texts above. */
export class EngineError extends Error {}

const NO_RECEIVER = /Receiving end does not exist|Could not establish connection/i;

/**
 * @param sendMessage  chrome.runtime.sendMessage
 * @returns ask(message) -> the answer's value; throws EngineError
 */
export function createAsk(sendMessage, { attempts = 25, sleep = ms => new Promise(r => setTimeout(r, ms)) } = {}) {
  return async function ask(message) {
    for (let attempt = 1; ; attempt++) {
      let answer;
      try {
        answer = await sendMessage(message);
      } catch (e) {
        if (!NO_RECEIVER.test(String(e?.message ?? e))) throw new EngineError(FAILED);
        if (attempt >= attempts) throw new EngineError(NOT_RUNNING);
        await sleep(200);
        continue;
      }
      if (!answer) throw new EngineError(FAILED);
      if (!answer.ok) throw new EngineError(answer.error);
      return answer.value;
    }
  };
}
