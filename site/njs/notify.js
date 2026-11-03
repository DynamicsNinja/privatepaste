// POST /notify: the homepage's "Hosted version: get notified" sign-up, run by nginx's njs module (nginx.conf). It
// appends {email, at} as one JSON line to a file on the homepage's own volume, and nothing else: no IP address, no
// user agent. Plain njs (no destructuring, classes or optional chaining, which its engine lacks), and importable by
// Node too, so site/test/notify.test.mjs tests this very file.
import fs from 'fs';

const FILE = '/data/signups/hosted.jsonl';
const MAX_EMAIL = 254;
const MAX_LOCAL = 64;
// Something@something.tld, without spaces, quotes, brackets or commas. Not RFC 5322, just enough to catch typos.
const EMAIL = /^[^\s@"'<>()\[\],;:\\]+@[^\s@"'<>()\[\],;:\\.]+(\.[^\s@"'<>()\[\],;:\\.]+)*\.[^\s@"'<>()\[\],;:\\.]{2,}$/;

// application/x-www-form-urlencoded, first value of each name.
function parseForm(body) {
    const form = {};
    const pairs = String(body || '').split('&');
    for (let i = 0; i < pairs.length; i++) {
        if (!pairs[i]) continue;
        const eq = pairs[i].indexOf('=');
        const name = decode(eq < 0 ? pairs[i] : pairs[i].slice(0, eq));
        const value = decode(eq < 0 ? '' : pairs[i].slice(eq + 1));
        if (name !== null && value !== null && !Object.prototype.hasOwnProperty.call(form, name)) form[name] = value;
    }
    return form;
}

function decode(s) {
    try {
        return decodeURIComponent(s.replace(/\+/g, ' '));
    } catch (e) {
        return null;
    }
}

function normalizeEmail(raw) {
    return String(raw || '').trim().toLowerCase();
}

function validEmail(email) {
    return email.length <= MAX_EMAIL && email.indexOf('@') <= MAX_LOCAL && EMAIL.test(email);
}

// 'ok', 'invalid-email' or 'no-consent', and the address to keep when it is 'ok' and not the honeypot.
function check(form) {
    const email = normalizeEmail(form.email);
    if (!validEmail(email)) return { result: 'invalid-email' };
    if (form.consent !== 'yes') return { result: 'no-consent' };
    // The hidden field only a bot fills in: it hears "ok" and nothing is kept.
    if (form.website) return { result: 'ok' };
    return { result: 'ok', email: email };
}

function signupLine(email, now) {
    return JSON.stringify({ email: email, at: now.toISOString() }) + '\n';
}

function alreadySignedUp(text, email) {
    const lines = text.split('\n');
    for (let i = 0; i < lines.length; i++) {
        if (!lines[i]) continue;
        try {
            if (JSON.parse(lines[i]).email === email) return true;
        } catch (e) {
            // A line someone edited by hand: skip it rather than refuse every sign-up.
        }
    }
    return false;
}

// Checks the form and keeps the address once. A repeat sign-up also hears "ok", so the form never tells anyone
// whether an address is already on the list. deps: file, now(), readFile(path), appendFile(path, line).
function save(form, deps) {
    const checked = check(form);
    if (!checked.email) return checked.result;
    try {
        let text = '';
        try {
            text = deps.readFile(deps.file);
        } catch (e) {
            if (e.code !== 'ENOENT') throw e;
        }
        if (!alreadySignedUp(text, checked.email)) deps.appendFile(deps.file, signupLine(checked.email, deps.now()));
        return 'ok';
    } catch (e) {
        return 'failed';
    }
}

const STATUS = { 'ok': 200, 'invalid-email': 400, 'no-consent': 400, 'too-many': 429, 'failed': 503 };

// fetch() from site.js asks for JSON; a plain form post (no JavaScript) is sent back to the form, where the
// fragment shows the matching message (style.css, :target).
function reply(r, result) {
    const accept = r.headersIn['Accept'] || '';
    if (accept.indexOf('application/json') >= 0) {
        r.headersOut['Content-Type'] = 'application/json';
        r.return(STATUS[result], JSON.stringify({ result: result }));
    } else {
        r.return(303, '/#notify-' + result);
    }
}

function realDeps() {
    return {
        file: FILE,
        now: function () { return new Date(); },
        readFile: function (path) { return fs.readFileSync(path, 'utf8'); },
        appendFile: function (path, line) { fs.appendFileSync(path, line, { mode: 384 } /* 0600 */); },
    };
}

function submit(r) {
    if (r.method !== 'POST') {
        r.headersOut['Allow'] = 'POST';
        r.return(405);
        return;
    }
    reply(r, save(parseForm(r.requestText), realDeps()));
}

// limit_req's 429, through error_page.
function limited(r) {
    reply(r, 'too-many');
}

export default {
    submit: submit,
    limited: limited,
    FILE: FILE,
    parseForm: parseForm,
    validEmail: validEmail,
    check: check,
    signupLine: signupLine,
    alreadySignedUp: alreadySignedUp,
    save: save,
    reply: reply,
};
