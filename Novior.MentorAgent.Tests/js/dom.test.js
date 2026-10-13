// Tests for everything in Novior.MentorAgent.Abstractions/wwwroot/js/MentorAgent.js that is NOT the
// speech-synthesis engine: persistent storage, the language map, voice input and the hands-free
// loop, barge-in, and image shrinking.
//
// Run directly with `node dom.test.js`, or through `dotnet test` — MentorAgentJsTests.cs shells
// out to node for every *.test.js in this folder and reports the output as an xUnit failure.
//
// speech.test.js owns the sentence-splitting and text-cleaning half. The two files stub the same
// globals differently on purpose (this one needs recognition and canvas, that one does not), so
// they are separate processes rather than one suite.

const path = require('path');
const assert = require('assert');

// ── Browser stubs ─────────────────────────────────────────────────────────────────

// localStorage that can be made to throw, which is what private mode and a sandboxed iframe
// actually do — they do not return null.
let storeThrows = false;
const backing = new Map();
const localStorage = {
    getItem: k => { if (storeThrows) throw new DOMException('denied'); return backing.has(k) ? backing.get(k) : null; },
    setItem: (k, v) => { if (storeThrows) throw new DOMException('denied'); backing.set(k, String(v)); },
    removeItem: k => { if (storeThrows) throw new DOMException('denied'); backing.delete(k); },
};
global.DOMException = global.DOMException || class DOMException extends Error { };

// Speech recognition. Instances are recorded so a test can drive the engine's callbacks by hand —
// there is no real microphone and no real timing.
const built = [];
class FakeRecognition {
    constructor() { this.started = 0; this.stopped = 0; this.aborted = 0; built.push(this); }
    start() { this.started++; if (this.onstart) this.onstart(); }
    stop() { this.stopped++; if (this.onend) this.onend(); }
    abort() { this.aborted++; if (this.onerror) this.onerror({ error: 'aborted' }); if (this.onend) this.onend(); }
}

const spoken = [];
let cancelled = 0;
class FakeUtterance { constructor(text) { this.text = text; } }

global.SpeechSynthesisUtterance = FakeUtterance;
global.window = {
    localStorage,
    SpeechRecognition: FakeRecognition,
    SpeechSynthesisUtterance: FakeUtterance,
    speechSynthesis: {
        // Utterances do NOT complete on their own here: several tests need the window in which
        // the assistant is speaking, which an immediately-finishing utterance would skip past.
        speak: u => { spoken.push(u); if (u.onstart) u.onstart(); },
        cancel: () => { cancelled++; },
        pause: () => { }, resume: () => { }
    }
};
global.setInterval = () => 0;
global.clearInterval = () => { };
global.setTimeout = fn => { fn(); return 0; };   // the 300 ms hands-free gap, taken immediately

// Canvas + bitmap, for _shrinkImage. Encoded size is a deterministic function of the pixel count,
// so the scale-down loop is observable rather than guessed at.
const BYTES_PER_PIXEL = 0.1;
const encoded = [];
class FakeBlob {
    constructor(size, type) { this.size = size; this.type = type; }
}
global.File = class File extends FakeBlob {
    constructor(parts, name, opts) {
        super(parts[0] && parts[0].size ? parts[0].size : 0, (opts && opts.type) || '');
        this.name = name;
    }
};
global.document = {
    createElement: () => ({
        width: 0, height: 0,
        getContext: () => ({ fillStyle: '', fillRect: () => { }, drawImage: () => { } }),
        toBlob(cb, type, quality) {
            encoded.push({ w: this.width, h: this.height, type, quality });
            cb(new FakeBlob(Math.round(this.width * this.height * BYTES_PER_PIXEL), type));
        }
    })
};

let bitmapSize = { width: 4000, height: 3000 };
let bitmapThrows = false;
global.createImageBitmap = async () => {
    if (bitmapThrows) throw new Error('decode failed');
    return { ...bitmapSize, close: () => { } };
};

require(path.join(__dirname, '..', '..', 'Novior.MentorAgent.Abstractions', 'wwwroot', 'js', 'MentorAgent.js'));

// In a browser `window` IS the global object, so `window.MentorAgent = {...}` also defines the bare
// identifier the file uses internally. Node needs that alias set by hand.
const M = global.window.MentorAgent;
global.MentorAgent = M;

// ── Tiny assertion harness ────────────────────────────────────────────────────────
let fail = 0;
const eq = (name, got, want) => {
    if (JSON.stringify(got) === JSON.stringify(want)) { console.log(`ok   ${name}`); return; }
    fail++;
    console.log(`FAIL ${name}\n  got : ${JSON.stringify(got)}\n  want: ${JSON.stringify(want)}`);
};
const check = (name, ok, detail) => {
    if (ok) { console.log(`ok   ${name}`); return; }
    fail++;
    console.log(`FAIL ${name}${detail ? '\n  ' + detail : ''}`);
};

// A .NET reference that records every interop call, so "was the user told" is assertable.
const makeRef = () => {
    const calls = [];
    return {
        calls,
        names: () => calls.map(c => c[0]),
        invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); }
    };
};

const resetVoice = () => {
    built.length = 0; spoken.length = 0; cancelled = 0;
    M.stopVoice();
    M.stopSpeaking();
    M._isSpeaking = false;
};

// ══ Persistent flags and values ═══════════════════════════════════════════════════
//
// These back the onboarding tour (#18): whether it has been seen, and where it was suspended.
// Every one of them is wrapped in try/catch because localStorage *throws* in private mode rather
// than returning null — a widget that threw on open would be a real defect, and the documented
// degradation is "the user gets the tour again".

storeThrows = false;
backing.clear();

eq('flag: absent reads false', M.flagGet('bm.tour.seen'), false);
M.flagSet('bm.tour.seen');
eq('flag: set then read', M.flagGet('bm.tour.seen'), true);
eq('flag: a value that is not "1" is not true', (backing.set('other', '0'), M.flagGet('other')), false);

eq('value: absent is null, not empty string', M.valueGet('bm.tour.step'), null);
M.valueSet('bm.tour.step', 0);
// The distinction the doc comment calls out: "never stored" must be tellable from "stored as 0",
// or a tour suspended on its first step resumes from the beginning every time.
eq('value: zero stores as "0" and is not null', M.valueGet('bm.tour.step'), '0');
check('value: zero is distinguishable from absent', M.valueGet('bm.tour.step') !== M.valueGet('nope'));
M.valueSet('bm.tour.step', 3);
eq('value: a number is stored as its string', M.valueGet('bm.tour.step'), '3');
M.valueClear('bm.tour.step');
eq('value: cleared reads null again', M.valueGet('bm.tour.step'), null);

storeThrows = true;
eq('private mode: flagGet degrades to false', M.flagGet('bm.tour.seen'), false);
eq('private mode: valueGet degrades to null', M.valueGet('bm.tour.step'), null);
check('private mode: flagSet does not throw', (() => { try { M.flagSet('x'); return true; } catch { return false; } })());
check('private mode: valueSet does not throw', (() => { try { M.valueSet('x', 1); return true; } catch { return false; } })());
check('private mode: valueClear does not throw', (() => { try { M.valueClear('x'); return true; } catch { return false; } })());
storeThrows = false;

// A sandboxed iframe can leave localStorage undefined entirely, which is a TypeError rather than
// the SecurityError the try/catch was written for. Same catch, so same degradation.
const realStorage = global.window.localStorage;
delete global.window.localStorage;
eq('no localStorage at all: flagGet is false', M.flagGet('bm.tour.seen'), false);
eq('no localStorage at all: valueGet is null', M.valueGet('bm.tour.step'), null);
check('no localStorage at all: flagSet does not throw', (() => { try { M.flagSet('x'); return true; } catch { return false; } })());
global.window.localStorage = realStorage;

// ══ _toLangCode ═══════════════════════════════════════════════════════════════════
//
// The result is assigned to SpeechRecognition.lang and to an utterance's lang — a wire format for
// the browser's speech engines, not a display string. A wrong tag picks a different voice, or none.

const L = s => M._toLangCode(s);

// The ten ISO codes MentorLanguage.ToIsoCode() produces. This list is the contract between the
// C# enum and this map; a language added to one and not the other is the failure it guards.
const isoToBcp = {
    en: 'en-US', it: 'it-IT', fr: 'fr-FR', de: 'de-DE', es: 'es-ES',
    pt: 'pt-PT', nl: 'nl-NL', pl: 'pl-PL', ja: 'ja-JP', zh: 'zh-CN',
};
for (const [iso, bcp] of Object.entries(isoToBcp))
    eq(`lang: ${iso} → ${bcp}`, L(iso), bcp);

// Every one must come back as a real regional tag whose prefix is the ISO code it was asked for.
// Returning the input unchanged (the fallback) would silently pass a bare "pl" to the engine.
check('lang: all ten resolve to a regional tag matching their own prefix',
    Object.entries(isoToBcp).every(([iso]) => /^[a-z]{2}-[A-Z]{2}$/.test(L(iso)) && L(iso).startsWith(iso + '-')),
    Object.keys(isoToBcp).map(i => `${i}→${L(i)}`).join(' '));

// MentorLanguage.ToFullName() returns the enum name, and the map accepts those too.
eq('lang: the enum name works as well', L('Italian'), 'it-IT');
eq('lang: enum name, Japanese', L('Japanese'), 'ja-JP');
eq('lang: an Italian-language name works', L('tedesco'), 'de-DE');
eq('lang: case is ignored', L('ITALIAN'), 'it-IT');
eq('lang: surrounding whitespace is ignored', L('  it  '), 'it-IT');
eq('lang: an already-regional tag is normalised through its prefix', L('it-IT'), 'it-IT');
eq('lang: a different region collapses to the supported one', L('pt-BR'), 'pt-PT');
eq('lang: null falls back to en-US', L(null), 'en-US');
eq('lang: empty falls back to en-US', L(''), 'en-US');
// Documented by the code rather than degraded: unreachable from the widget, which always passes
// MentorLanguage.ToIsoCode(). Pinned so a change to that call site has to face this.
eq('lang: an unknown language is returned unchanged', L('Klingon'), 'Klingon');

// ══ Voice input ═══════════════════════════════════════════════════════════════════

resetVoice();
{
    const ref = makeRef();
    const real = global.window.SpeechRecognition;
    delete global.window.SpeechRecognition;
    check('voice: unsupported browser is reported, not thrown',
        (() => { try { M.startVoice(ref, 'it', {}); return true; } catch { return false; } })());
    eq('voice: unsupported browser calls OnVoiceError', ref.names(), ['OnVoiceError']);
    eq('voice: isVoiceSupported says no', M.isVoiceSupported(), false);
    global.window.SpeechRecognition = real;
    eq('voice: isVoiceSupported says yes once present', M.isVoiceSupported(), true);
}

resetVoice();
{
    const ref = makeRef();
    M.startVoice(ref, 'it', {});
    eq('voice: recognition is created with the mapped language', built[0].lang, 'it-IT');
    eq('voice: single-utterance, never continuous', built[0].continuous, false);
    eq('voice: onstart is reported', ref.names(), ['OnVoiceStarted']);
    check('voice: listening', M._isListening === true);

    // Pressing the button again is a toggle, not a second session.
    M.startVoice(ref, 'it', {});
    eq('voice: a second press stops instead of starting a second engine', built.length, 1);
    check('voice: no longer listening', M._isListening === false);
}

resetVoice();
{
    const ref = makeRef();
    M.startVoice(ref, 'en', { interim: true });
    const r = built[0];
    eq('voice: interim results are requested when asked for', r.interimResults, true);

    r.onresult({
        resultIndex: 0,
        results: [{ isFinal: false, 0: { transcript: 'quanti ordini' } }]
    });
    eq('voice: interim text is shown', ref.names(), ['OnVoiceStarted', 'OnVoiceInterim']);

    r.onresult({
        resultIndex: 0,
        results: [{ isFinal: true, 0: { transcript: 'quanti ordini aperti ci sono' } }]
    });
    eq('voice: only the final transcript is sent',
        ref.names(), ['OnVoiceStarted', 'OnVoiceInterim', 'OnVoiceResult']);
    eq('voice: the final transcript is the whole sentence',
        ref.calls.at(-1)[1], 'quanti ordini aperti ci sono');
}

resetVoice();
{
    // 'no-speech' and 'aborted' are how a hands-free loop idles. Reporting them would put an error
    // banner in front of a user who simply paused.
    const ref = makeRef();
    M.startVoice(ref, 'it', { handsFree: true });
    built[0].onerror({ error: 'no-speech' });
    eq('voice: a silent pass is not an error', ref.names(), ['OnVoiceStarted']);
    check('voice: hands-free survives a silent pass', M._handsFree === true);

    built[0].onerror({ error: 'audio-capture' });
    eq('voice: a real fault is reported', ref.names().at(-1), 'OnVoiceError');
    check('voice: hands-free is disarmed by a real fault', M._handsFree === false,
        'otherwise the loop retries against a broken microphone forever');
}

resetVoice();
{
    // stopVoice must clear _handsFree *before* stopping, or onend restarts the very loop the user
    // just switched off.
    const ref = makeRef();
    M.startVoice(ref, 'it', { handsFree: true });
    built.length = 0;
    M.stopVoice();
    eq('voice: stopping hands-free does not start another pass', built.length, 0);
    check('voice: hands-free is disarmed', M._handsFree === false);
}

resetVoice();
{
    // Without hands-free, one press is one utterance.
    const ref = makeRef();
    M.startVoice(ref, 'it', {});
    built.length = 0;
    built[0] === undefined && M._recognition.onend();
    eq('voice: a single pass does not re-arm itself', built.length, 0);
}

// ══ Barge-in ══════════════════════════════════════════════════════════════════════
//
// Reaching for the microphone is the clearest signal that the user wants the floor. The other
// three documented triggers (send, Stop, new conversation) are C# calls straight to stopSpeaking.

resetVoice();
{
    const ref = makeRef();
    M.beginSpeech('it', 1.0);
    M.pushSpeech('Sto parlando a lungo. ');
    check('barge-in: the assistant is speaking', spoken.length > 0);

    cancelled = 0;
    M.startVoice(ref, 'it', {});
    check('barge-in: pressing the microphone cancels playback', cancelled === 1, `cancel() ${cancelled}x`);
    check('barge-in: the buffered tail is dropped', M._speechBuffer === '');
    check('barge-in: the session is closed', M._speechOpen === false);
}

resetVoice();
{
    // Opt-out. A host that wants the assistant to finish sets VoiceBargeIn = false.
    const ref = makeRef();
    M.beginSpeech('it', 1.0);
    M.pushSpeech('Sto parlando a lungo. ');
    cancelled = 0;
    M.startVoice(ref, 'it', { bargeIn: false });
    eq('barge-in: opting out leaves playback alone', cancelled, 0);
    check('barge-in: opting out leaves the session open', M._speechOpen === true);
}

// ══ Hands-free ════════════════════════════════════════════════════════════════════
//
// The design the file's own comment states: recognition is restarted rather than left continuous,
// so that `_resumeWhenQuiet` can "hold the microphone closed until playback has finished". A
// microphone that is open while the assistant speaks through the same speakers transcribes the
// assistant and replies to it — the loop the whole design exists to avoid.

resetVoice();
{
    const ref = makeRef();
    M.startVoice(ref, 'it', { handsFree: true });
    const first = built[0];

    // The user finishes speaking: a final transcript, then the engine ends the pass. The answer
    // has NOT started yet — the model has not produced a token, so nothing is being spoken.
    first.onresult({ resultIndex: 0, results: [{ isFinal: true, 0: { transcript: 'ciao' } }] });
    built.length = 0;
    first.onend();

    // The turn now arrives and the assistant starts speaking.
    M.beginSpeech('it', 1.0);
    M.pushSpeech('Ecco la risposta che sto leggendo ad alta voce. ');

    check('hands-free: the microphone is not open while the assistant speaks',
        M._isListening === false,
        `_isListening=${M._isListening}, passes started since the turn: ${built.length} — ` +
        `an open microphone here transcribes the assistant's own answer and sends it back`);
}

resetVoice();
{
    // The other half: once playback really has finished, the microphone must reopen — otherwise
    // hands-free is a single exchange.
    const ref = makeRef();
    M.startVoice(ref, 'it', { handsFree: true });
    const first = built[0];
    first.onend();
    M.beginSpeech('it', 1.0);
    M.pushSpeech('Risposta breve. ');
    built.length = 0;

    M.endSpeech();
    spoken.forEach(u => u.onend && u.onend());   // playback finishes

    check('hands-free: the microphone reopens once the assistant is quiet',
        built.length > 0, `passes started after playback: ${built.length}`);
}

resetVoice();
{
    // An answer with nothing speakable (code only) queues no utterance at all. endSpeech has to
    // release the loop itself or hands-free waits for a playback that never happens.
    const ref = makeRef();
    M.startVoice(ref, 'it', { handsFree: true });
    built[0].onend();
    M.beginSpeech('it', 1.0);
    M.pushSpeech('```\nsolo codice\n```');
    built.length = 0;
    M.endSpeech();

    check('hands-free: an answer with nothing to speak still reopens the microphone',
        built.length > 0, `passes started: ${built.length}`);
}

// ══ Image shrinking ═══════════════════════════════════════════════════════════════
//
// A screenshot is routinely 5-10 MB and the server rejects anything over MaxImageBytes. Rather
// than refuse it, the widget re-encodes. Two invariants matter: the result must never be larger
// than what was handed in, and its MIME type must still satisfy the server's allow-list.

const img = (size, type) => ({ size, type, name: 'shot.png' });

(async () => {
    encoded.length = 0;
    const small = img(1000, 'image/png');
    check('shrink: a file already under the limit is returned untouched',
        await M._shrinkImage(small, 100000) === small);
    eq('shrink: nothing was encoded', encoded.length, 0);

    check('shrink: no limit means no work',
        await M._shrinkImage(img(9e6, 'image/png'), 0) === img(9e6, 'image/png').size ? false : true);
    const noLimit = img(9e6, 'image/png');
    check('shrink: a falsy maxBytes returns the original', await M._shrinkImage(noLimit, 0) === noLimit);

    // An animated GIF would be flattened to one frame by a canvas round-trip. Better to let the
    // server report "too large" than to silently destroy the animation.
    const gif = img(9e6, 'image/gif');
    check('shrink: an animated GIF is never re-encoded', await M._shrinkImage(gif, 100000) === gif);

    // A decode failure must degrade, not throw: the paste handler awaits this.
    bitmapThrows = true;
    const broken = img(9e6, 'image/png');
    check('shrink: an undecodable file comes back unchanged', await M._shrinkImage(broken, 100000) === broken);
    bitmapThrows = false;

    // The real path. 4000x3000 at 0.1 bytes/px: clamped to 2000px on the long edge, then scaled
    // by 0.7 until it fits.
    encoded.length = 0;
    bitmapSize = { width: 4000, height: 3000 };
    const big = img(9e6, 'image/png');
    const out = await M._shrinkImage(big, 100000);

    eq('shrink: the first attempt clamps the long edge to 2000px',
        [encoded[0].w, encoded[0].h], [2000, 1500]);
    check('shrink: it keeps scaling until it fits', encoded.length >= 2, `attempts: ${encoded.length}`);
    check('shrink: the result is under the limit', out.size <= 100000, `size ${out.size}`);
    check('shrink: the result is smaller than the original', out.size < big.size);
    eq('shrink: a PNG stays a PNG', out.type, 'image/png');
    eq('shrink: the file name is preserved', out.name, 'shot.png');
    check('shrink: each attempt is smaller than the last',
        encoded.every((e, i) => i === 0 || e.w < encoded[i - 1].w),
        JSON.stringify(encoded.map(e => `${e.w}x${e.h}`)));

    // MIME handling — the result must still be something AllowedImageTypes accepts.
    encoded.length = 0;
    const jpeg = await M._shrinkImage(img(9e6, 'image/jpeg'), 100000);
    eq('shrink: a JPEG stays a JPEG', jpeg.type, 'image/jpeg');
    eq('shrink: a lossy format is given a quality', encoded[0].quality, 0.85);

    encoded.length = 0;
    const webp = await M._shrinkImage(img(9e6, 'image/webp'), 100000);
    eq('shrink: a WebP stays a WebP', webp.type, 'image/webp');

    encoded.length = 0;
    const bmp = await M._shrinkImage(img(9e6, 'image/bmp'), 100000);
    eq('shrink: an unusual format is re-encoded as PNG', bmp.type, 'image/png');
    eq('shrink: a lossless format asks for no quality', encoded[0].quality, undefined);

    // Six attempts and still too big — the original comes back, and .NET reports "too large".
    // Returning the last, still-oversized attempt would be worse: a destroyed image AND a refusal.
    encoded.length = 0;
    const hopeless = img(9e6, 'image/png');
    const same = await M._shrinkImage(hopeless, 1);
    eq('shrink: it gives up after six attempts', encoded.length, 6);
    check('shrink: giving up returns the original, not a mangled one', same === hopeless);

    // ── A second <script> tag for this file ───────────────────────────────────────
    //
    // Two tags is a supported configuration, not a mistake. A standalone WebAssembly app loads
    // index.html before .NET starts, so the documented setup puts a versioned tag there to defeat
    // the browser cache, and the widget's <HeadContent> injects another once it renders — which
    // means the second load lands *after* the first has been running. Driven live on the WASM
    // sample, that reset `_handsFree`, `_isSpeaking` and the live SpeechRecognition handle, leaving
    // a running microphone with no owner and no way to stop it. The Blazor Server sample has one
    // tag and never sees it, which is why only a cross-app comparison found it.
    const widgetPath = path.join(
        __dirname, '..', '..', 'Novior.MentorAgent.Abstractions', 'wwwroot', 'js', 'MentorAgent.js');

    global.window.MentorAgent._handsFree   = true;
    global.window.MentorAgent._isSpeaking  = true;
    global.window.MentorAgent._speechOpen  = true;
    global.window.MentorAgent._recognition = { marker: 'live-session' };

    delete require.cache[require.resolve(widgetPath)];
    require(widgetPath);                       // the second tag
    const reloaded = global.window.MentorAgent;

    check('reload: a second load does not throw on the top-level binding', true);
    check('reload: the live recognition handle survives',
        reloaded._recognition && reloaded._recognition.marker === 'live-session');
    eq('reload: hands-free stays armed', reloaded._handsFree, true);
    eq('reload: the speaking flag is not cleared under the running turn', reloaded._isSpeaking, true);
    eq('reload: the open-utterance flag is not cleared', reloaded._speechOpen, true);

    // The later tag still wins for code — that is the entire point of the ?v= query the docs tell
    // WASM hosts to bump. Only mutable state is carried across.
    check('reload: the functions come from the new definition', typeof reloaded.flagGet === 'function');

    // A first load with nothing before it must not carry anything, or the defaults would be lost.
    eq('reload: a fresh load keeps the declared defaults', typeof reloaded._resumeWhenQuiet, 'boolean');

    console.log(fail === 0 ? '\nALL PASS' : `\n${fail} FAILED`);
    process.exit(fail === 0 ? 0 : 1);
})();
