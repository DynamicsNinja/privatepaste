// The homepage's sign-up handler (site/njs/notify.js), run under Node with a fake file and a fake njs request.
//   node --test "site/test/*.test.mjs"
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import notify from '../njs/notify.js';

const NOW = new Date('2026-11-03T10:20:30.000Z');

function memoryDeps(initial = '') {
    const deps = {
        file: '/data/signups/hosted.jsonl',
        text: initial,
        appends: 0,
        now: () => NOW,
        readFile: () => {
            if (deps.text === null) throw Object.assign(new Error('no file'), { code: 'ENOENT' });
            return deps.text;
        },
        appendFile: (path, line) => { deps.text = (deps.text || '') + line; deps.appends++; },
    };
    return deps;
}

const form = (email, extra = {}) => ({ email, consent: 'yes', ...extra });

test('parses a urlencoded form body', () => {
    assert.deepEqual(
        notify.parseForm('email=Me%40Example.COM&consent=yes&website=&x=a+b'),
        { email: 'Me@Example.COM', consent: 'yes', website: '', x: 'a b' });
    assert.deepEqual(notify.parseForm('email=a%40b.co&email=second'), { email: 'a@b.co' }, 'the first value wins');
    assert.deepEqual(notify.parseForm('email=%E0%A4%A&consent=yes'), { consent: 'yes' }, 'a malformed value is dropped');
    assert.deepEqual(notify.parseForm(undefined), {});
    assert.deepEqual(notify.parseForm(''), {});
});

test('accepts ordinary addresses and rejects the rest', () => {
    for (const ok of ['me@example.com', 'first.last+tag@mail.example.co.uk', 'x@xn--p1ai.xn--p1ai', 'a_b-c@sub-domain.io']) {
        assert.equal(notify.validEmail(ok), true, ok);
    }
    const tooLong = `${'a'.repeat(60)}@${'b'.repeat(190)}.com`;
    const longLocal = `${'a'.repeat(65)}@example.com`;
    for (const bad of ['', 'me', 'me@', '@example.com', 'me@example', 'me@example.c', 'me@@example.com', 'me @example.com',
        'me@exa mple.com', 'me@example..com', 'me@.example.com', '"me"@example.com', 'me@example.com,you@example.com',
        '<me@example.com>', 'me@example.com\n', tooLong, longLocal]) {
        assert.equal(notify.validEmail(bad), false, JSON.stringify(bad));
    }
});

test('keeps a new address as one JSON line: lowercased, trimmed, with the time', () => {
    const deps = memoryDeps(null);
    assert.equal(notify.save(form('  Me@Example.COM '), deps), 'ok');
    assert.equal(deps.text, '{"email":"me@example.com","at":"2026-11-03T10:20:30.000Z"}\n');
    assert.deepEqual(Object.keys(JSON.parse(deps.text)), ['email', 'at']);
});

test('stores nothing but the address and the time, whatever the form sends', () => {
    const deps = memoryDeps('');
    notify.save(form('me@example.com', { ip: '203.0.113.9', ua: 'Mozilla/5.0', name: 'Me', extra: 'x' }), deps);
    const line = JSON.parse(deps.text);
    assert.deepEqual(line, { email: 'me@example.com', at: NOW.toISOString() });
    assert.doesNotMatch(deps.text, /203\.0\.113\.9|Mozilla/);
});

test('a repeat sign-up is ok but not stored twice', () => {
    const deps = memoryDeps('');
    assert.equal(notify.save(form('me@example.com'), deps), 'ok');
    assert.equal(notify.save(form(' ME@example.com'), deps), 'ok');
    assert.equal(notify.save(form('you@example.com'), deps), 'ok');
    assert.equal(deps.appends, 2);
    assert.deepEqual(deps.text.trim().split('\n').map(l => JSON.parse(l).email), ['me@example.com', 'you@example.com']);
});

test('a hand-edited line does not stop sign-ups', () => {
    const deps = memoryDeps('not json\n{"email":"me@example.com","at":"x"}\n\n');
    assert.equal(notify.save(form('me@example.com'), deps), 'ok');
    assert.equal(deps.appends, 0);
    assert.equal(notify.save(form('new@example.com'), deps), 'ok');
    assert.equal(deps.appends, 1);
});

test('an invalid address or missing consent is refused and nothing is stored', () => {
    const deps = memoryDeps('');
    assert.equal(notify.save(form('not an email'), deps), 'invalid-email');
    assert.equal(notify.save({ consent: 'yes' }, deps), 'invalid-email');
    assert.equal(notify.save({ email: 'me@example.com' }, deps), 'no-consent');
    assert.equal(notify.save({ email: 'me@example.com', consent: 'on' }, deps), 'no-consent');
    assert.equal(deps.appends, 0);
});

test('a filled honeypot hears ok and nothing is stored', () => {
    const deps = memoryDeps('');
    assert.equal(notify.save(form('bot@example.com', { website: 'http://spam.example' }), deps), 'ok');
    assert.equal(deps.appends, 0);
    assert.equal(deps.text, '');
});

test('a file that cannot be read or written fails without throwing', () => {
    const unreadable = memoryDeps('');
    unreadable.readFile = () => { throw Object.assign(new Error('denied'), { code: 'EACCES' }); };
    assert.equal(notify.save(form('me@example.com'), unreadable), 'failed');
    assert.equal(unreadable.appends, 0);
    const unwritable = memoryDeps(null);
    unwritable.appendFile = () => { throw Object.assign(new Error('no volume'), { code: 'ENOENT' }); };
    assert.equal(notify.save(form('me@example.com'), unwritable), 'failed');
});

test('writes through the real fs module (what njs uses), appending', () => {
    const dir = mkdtempSync(join(tmpdir(), 'pp-notify-'));
    try {
        const file = join(dir, 'hosted.jsonl');
        const deps = { file, now: () => NOW,
            readFile: p => readFileSync(p, 'utf8'),
            appendFile: (p, line) => writeFileSync(p, line, { flag: 'a' }) };
        assert.equal(notify.save(form('one@example.com'), deps), 'ok');
        assert.equal(notify.save(form('two@example.com'), deps), 'ok');
        assert.equal(notify.save(form('one@example.com'), deps), 'ok');
        assert.equal(readFileSync(file, 'utf8'),
            '{"email":"one@example.com","at":"2026-11-03T10:20:30.000Z"}\n{"email":"two@example.com","at":"2026-11-03T10:20:30.000Z"}\n');
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

function fakeRequest(method, body, accept) {
    return {
        method,
        requestText: body,
        headersIn: accept ? { Accept: accept } : {},
        headersOut: {},
        returned: null,
        return(status, text) { this.returned = { status, text }; },
    };
}

test('a plain form post is sent back to the form with the result in the fragment', () => {
    for (const [result, fragment] of [['ok', '#notify-ok'], ['invalid-email', '#notify-invalid-email'],
        ['no-consent', '#notify-no-consent'], ['too-many', '#notify-too-many'], ['failed', '#notify-failed']]) {
        const r = fakeRequest('POST', '', 'text/html,application/xhtml+xml');
        notify.reply(r, result);
        assert.deepEqual(r.returned, { status: 303, text: `/${fragment}` });
    }
});

test('fetch gets JSON with a matching status', () => {
    for (const [result, status] of [['ok', 200], ['invalid-email', 400], ['no-consent', 400], ['too-many', 429], ['failed', 503]]) {
        const r = fakeRequest('POST', '', 'application/json');
        notify.reply(r, result);
        assert.equal(r.headersOut['Content-Type'], 'application/json');
        assert.deepEqual(r.returned, { status, text: JSON.stringify({ result }) });
    }
});

test('the rate-limit handler answers too-many', () => {
    const r = fakeRequest('POST', 'email=me%40example.com&consent=yes', 'application/json');
    notify.limited(r);
    assert.deepEqual(r.returned, { status: 429, text: '{"result":"too-many"}' });
});

test('only POST is accepted', () => {
    const r = fakeRequest('GET', undefined, 'text/html');
    notify.submit(r);
    assert.equal(r.returned.status, 405);
    assert.equal(r.headersOut.Allow, 'POST');
});

test('submit with an invalid form answers without touching the file', () => {
    const r = fakeRequest('POST', 'email=nope&consent=yes', 'application/json');
    notify.submit(r);
    assert.deepEqual(r.returned, { status: 400, text: '{"result":"invalid-email"}' });
});

test('the file is the one on the signups volume', () => {
    assert.equal(notify.FILE, '/data/signups/hosted.jsonl');
});
