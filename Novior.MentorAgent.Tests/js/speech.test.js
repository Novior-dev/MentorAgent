// Tests for the streaming text-to-speech engine in
// Novior.MentorAgent.Abstractions/wwwroot/js/MentorAgent.js (roadmap #17).
//
// Run directly with `node speech.test.js`, or through `dotnet test` — VoiceSpeechJsTests.cs
// shells out to node and reports the output as an xUnit failure.
//
// Everything here is deterministic: speechSynthesis is stubbed, so no audio device, no browser
// and no timing are involved.

const path = require('path');

// ── Browser stubs ─────────────────────────────────────────────────────────────────
const spoken = [];
let cancelled = 0;

class FakeUtterance {
  constructor(text) { this.text = text; }
}

global.SpeechSynthesisUtterance = FakeUtterance;
global.window = {
  SpeechSynthesisUtterance: FakeUtterance,
  speechSynthesis: {
    // Utterances complete immediately: the queue is FIFO and its ordering is what we assert on.
    speak: u => { spoken.push(u.text); if (u.onstart) u.onstart(); if (u.onend) u.onend(); },
    cancel: () => { cancelled++; },
    pause: () => {},
    resume: () => {}
  }
};
global.setInterval = () => 0;
global.clearInterval = () => {};
global.setTimeout = fn => fn();

require(path.join(__dirname, '..', '..', 'Novior.MentorAgent.Abstractions', 'wwwroot', 'js', 'MentorAgent.js'));

// In a browser `window` IS the global object, so `window.MentorAgent = {...}` also defines the
// bare identifier the file uses internally. Node needs that alias set by hand.
const M = global.window.MentorAgent;
global.MentorAgent = M;

// ── Tiny assertion harness ────────────────────────────────────────────────────────
let fail = 0;
const eq = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  if (ok) { console.log(`ok   ${name}`); return; }
  fail++;
  console.log(`FAIL ${name}\n  got : ${JSON.stringify(got)}\n  want: ${JSON.stringify(want)}`);
};
const check = (name, ok, detail) => {
  if (ok) { console.log(`ok   ${name}`); return; }
  fail++;
  console.log(`FAIL ${name}${detail ? '\n  ' + detail : ''}`);
};

// ── _lastBoundary — index of the last character that safely ends a sentence, or -1 ─
const b = s => M._lastBoundary(s);

eq('boundary: sentence followed by a space',   b('Ciao mondo. Poi'), 10);
eq('boundary: none yet',                       b('Ciao mondo'), -1);
eq('boundary: terminator at end does not count', b('Ciao mondo.'), -1);
eq('boundary: a decimal point is not one',     b('Il totale e 8.459 euro'), -1);
eq('boundary: decimal then a real sentence',   b('Totale 8.459 euro. Altro'), 17);
eq('boundary: newline',                        b('Titolo\nCorpo'), 6);
eq('boundary: question mark',                  b('Che ore sono? Boh'), 12);
eq('boundary: ellipsis',                       b('Aspetta… ecco'), 7);
eq('boundary: the last of several',            b('Uno. Due. Tre'), 8);
eq('boundary: none inside a fenced block',     b('```\nsolo codice\n```'), -1);

// What is actually spoken is buf.slice(0, cut + 1) — check the sentence survives whole.
const cut = s => s.slice(0, b(s) + 1);
eq('slice: a whole sentence',   cut('Ciao mondo. Poi'), 'Ciao mondo.');
eq('slice: the decimal is kept', cut('Totale 8.459 euro. Altro'), 'Totale 8.459 euro.');

// ── _cleanTextForSpeech ───────────────────────────────────────────────────────────
const c = s => M._cleanTextForSpeech(s);

eq('clean: fenced code dropped',   c('Ecco:\n```js\nlet x=1;\n```\nfatto'), 'Ecco: fatto');
eq('clean: inline code dropped',   c('usa `npm run dev` ora'), 'usa ora');
eq('clean: bold unwrapped',        c('è **molto** importante'), 'è molto importante');
eq('clean: link text only',        c('vedi [la guida](http://x.y/z)'), 'vedi la guida');
eq('clean: table read as values',  c('| A | B |\n|---|---|\n| 1 | 2 |'), 'A, B, 1, 2');
eq('clean: heading marker dropped', c('## Titolo'), 'Titolo');

// ── Streaming: begin → push(n chunks) → end ───────────────────────────────────────
function runStream(answer, chunkSize) {
  spoken.length = 0;
  M.beginSpeech('it', 1.0);
  for (let i = 0; i < answer.length; i += chunkSize)
    M.pushSpeech(answer.slice(i, i + chunkSize));
  M.endSpeech();
  return spoken.slice();
}

// Compare the word stream, not where the cuts landed — chunk size must not change the content.
const words = s => M._cleanTextForSpeech(s).split(/[\s,]+/).filter(Boolean);

const answer =
  'Ecco il riepilogo. Il totale degli ordini in attesa e 8.459 euro, ' +
  'distribuiti su 12 clienti.\nVuoi che apra la lista?';

for (const size of [1, 3, 7, 40, 500]) {
  const out = runStream(answer, size);
  const got = words(out.join(' '));
  const want = words(answer);
  check(`stream (chunk ${size}): nothing lost or duplicated`,
        JSON.stringify(got) === JSON.stringify(want),
        `got ${got.length} words, want ${want.length}\n  got : ${got.join(' ')}\n  want: ${want.join(' ')}`);
  check(`stream (chunk ${size}): split into sentences`, out.length >= 2,
        `utterances: ${out.length}`);
}

check('stream: a decimal is never cut in half',
      runStream(answer, 7).some(u => u.includes('8.459')));

// A fenced block must be dropped whole — never spoken half-open.
const codeOut = runStream('Prova questo.\n```js\nlet x = 1;\n```\nFatto.', 4);
check('stream: code is never spoken', !codeOut.join(' ').includes('let x'), codeOut.join(' | '));
check('stream: the text around code is kept',
      codeOut.join(' ').includes('Prova questo') && codeOut.join(' ').includes('Fatto'),
      codeOut.join(' | '));

// ── Barge-in ──────────────────────────────────────────────────────────────────────
M.beginSpeech('it', 1.0);
M.pushSpeech('Questa frase viene interrotta a meta');
M.stopSpeaking();
spoken.length = 0;
M.pushSpeech(' e non deve mai essere pronunciata.');
M.endSpeech();
check('barge-in: the buffered tail is dropped', spoken.length === 0, JSON.stringify(spoken));

// ── A second beginSpeech inside one turn must not cancel the queue ─────────────────
// Regression: the widget set its "session open" flag *after* awaiting the interop call, so two
// chunks arriving back to back both opened a session. beginSpeech used to cancel unconditionally,
// so the second one silenced the sentence the first had just queued — the opening line of every
// answer went missing, which reads as "streaming voice does not work".
spoken.length = 0;
M.beginSpeech('it', 1.0);
M.pushSpeech('Prima frase da leggere. ');
const afterFirst = spoken.length;
cancelled = 0;                     // the opening begin clears the previous turn — that one is fine
M.beginSpeech('it', 1.0);          // the racing second call
M.pushSpeech('Seconda frase da leggere. ');
M.endSpeech();
check('double begin: the first sentence survives', afterFirst > 0 && spoken.length > afterFirst,
      `after first: ${afterFirst}, total: ${spoken.length} -> ${JSON.stringify(spoken)}`);
check('double begin: the racing call cancels nothing', cancelled === 0, `cancel() called ${cancelled}x`);
check('double begin: both sentences are spoken',
      spoken.join(' ').includes('Prima frase') && spoken.join(' ').includes('Seconda frase'),
      JSON.stringify(spoken));

// A genuine new turn — one that arrives after endSpeech — must still reset cleanly.
spoken.length = 0;
M.beginSpeech('it', 1.0);
M.pushSpeech('Turno nuovo. ');
M.endSpeech();
check('new turn after endSpeech: speaks normally', spoken.join(' ').includes('Turno nuovo'),
      JSON.stringify(spoken));

// ── An answer with nothing speakable must still settle ────────────────────────────
spoken.length = 0;
M.beginSpeech('it', 1.0);
M.pushSpeech('```\nsolo codice\n```');
M.endSpeech();
check('code-only answer: speaks nothing', spoken.length === 0, JSON.stringify(spoken));
// If this stayed true the hands-free loop would wait forever for a playback that never happens.
check('code-only answer: ends quiet', M.isSpeaking() === false);

console.log(fail === 0 ? '\nALL PASS' : `\n${fail} FAILED`);
process.exit(fail === 0 ? 0 : 1);
