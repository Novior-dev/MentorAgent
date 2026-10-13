// The widget's browser half. Assigned as one object literal, so a second <script> tag for this
// file replaces it wholesale — see the carry-over at the bottom of the file for why that matters.
// `var`, deliberately: a top-level `const` in a classic script is a global lexical binding, so the
// second <script> tag would throw "Identifier has already been declared" — which is the exact
// scenario this line exists to survive.
var __mentorAgentPrevious = window.MentorAgent;

// Not a namespace: the components call window.MentorAgent by name, so it stays when the .NET namespace
// changes (ContractNameTests).
window.MentorAgent = {

    // ── Scroll ────────────────────────────────────────────────────────────────
    scrollToBottom: (el) => {
        if (el) el.scrollTop = el.scrollHeight;
    },

    resetTextarea: (el) => {
        if (el) {
            el.style.height = 'auto';
            el.value = '';
        }
    },

    // ── Persistent flags (roadmap #18) ───────────────────────────────────────
    //
    // localStorage throws in some privacy modes and inside sandboxed iframes rather than returning
    // null, so both calls swallow. A user whose browser refuses to remember gets the tour again;
    // a widget that throws on open would be a real defect.
    flagGet: (key) => {
        try { return window.localStorage.getItem(key) === '1'; }
        catch { return false; }
    },

    flagSet: (key) => {
        try { window.localStorage.setItem(key, '1'); }
        catch { /* private mode — nothing to persist to */ }
    },

    // Same contract, for values that are not yes/no — the tour's resume position (#18).
    // Returns null when absent so the caller can tell "never stored" from "stored as 0".
    valueGet: (key) => {
        try { return window.localStorage.getItem(key); }
        catch { return null; }
    },

    valueSet: (key, value) => {
        try { window.localStorage.setItem(key, String(value)); }
        catch { /* private mode — nothing to persist to */ }
    },

    valueClear: (key) => {
        try { window.localStorage.removeItem(key); }
        catch { /* nothing to clear */ }
    },

    // ── Voice Input (Speech Recognition) — roadmap #17 ───────────────────────
    //
    // Recognition is always single-utterance (`continuous = false`), even in hands-free mode,
    // and hands-free is built by *restarting* it rather than leaving it open. That is
    // deliberate: a continuous session keeps the microphone live while the assistant is
    // speaking through the same speakers, so it transcribes its own answer and replies to
    // itself. Restarting lets `_resumeWhenQuiet` hold the microphone closed until playback
    // has finished.
    _recognition: null,
    _isListening: false,
    _handsFree: false,          // hands-free session armed — survives one recognition end
    _voiceRef: null,
    _voiceLang: 'en-US',
    _voiceInterim: false,

    startVoice: (dotnetRef, lang, opts) => {
        const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!SpeechRecognition) {
            dotnetRef.invokeMethodAsync('OnVoiceError', 'Speech recognition not supported in this browser.');
            return;
        }

        if (MentorAgent._isListening) {
            MentorAgent.stopVoice();
            return;
        }

        opts = opts || {};
        MentorAgent._voiceRef     = dotnetRef;
        MentorAgent._voiceLang    = MentorAgent._toLangCode(lang);
        MentorAgent._handsFree    = !!opts.handsFree;
        MentorAgent._voiceInterim = !!opts.interim;

        // Barge-in. Reaching for the microphone is the clearest possible signal that the user
        // wants the floor, so playback stops before recognition even starts — otherwise the
        // assistant talks over the person interrupting it, into the same microphone.
        if (opts.bargeIn !== false) MentorAgent.stopSpeaking();

        MentorAgent._listen();
    },

    // Builds and starts one recognition pass. Called again by the hands-free loop.
    _listen: () => {
        const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        const dotnetRef = MentorAgent._voiceRef;
        if (!SpeechRecognition || !dotnetRef) return;

        const recognition = new SpeechRecognition();
        recognition.lang = MentorAgent._voiceLang;
        recognition.continuous = false;
        recognition.interimResults = MentorAgent._voiceInterim;
        recognition.maxAlternatives = 1;

        recognition.onstart = () => {
            MentorAgent._isListening = true;
            dotnetRef.invokeMethodAsync('OnVoiceStarted');
        };

        recognition.onresult = (e) => {
            let final = '', interim = '';
            for (let i = e.resultIndex; i < e.results.length; i++) {
                const r = e.results[i];
                if (r.isFinal) final += r[0].transcript;
                else interim += r[0].transcript;
            }
            // Interim text is shown but never sent: it is rewritten as the engine hears more,
            // so submitting it would send a half-heard question.
            if (interim) dotnetRef.invokeMethodAsync('OnVoiceInterim', interim);
            if (final) dotnetRef.invokeMethodAsync('OnVoiceResult', final);
        };

        recognition.onerror = (e) => {
            MentorAgent._isListening = false;
            // 'no-speech' and 'aborted' are how a hands-free loop idles, not failures to report.
            if (e.error === 'no-speech' || e.error === 'aborted') return;
            MentorAgent._handsFree = false;
            dotnetRef.invokeMethodAsync('OnVoiceError', e.error);
        };

        recognition.onend = () => {
            MentorAgent._isListening = false;
            MentorAgent._recognition = null;
            dotnetRef.invokeMethodAsync('OnVoiceStopped');

            if (!MentorAgent._handsFree) return;
            // Hold the microphone shut while the answer is being spoken, then reopen it.
            if (MentorAgent._isSpeaking || MentorAgent._speechOpen) MentorAgent._resumeWhenQuiet = true;
            else setTimeout(() => { if (MentorAgent._handsFree) MentorAgent._listen(); }, 300);
        };

        MentorAgent._recognition = recognition;
        try { recognition.start(); }
        catch { MentorAgent._isListening = false; }   // already started — the engine throws
    },

    stopVoice: () => {
        MentorAgent._handsFree = false;          // must clear first, or onend restarts the loop
        MentorAgent._resumeWhenQuiet = false;
        if (MentorAgent._recognition) {
            MentorAgent._recognition.stop();
            MentorAgent._recognition = null;
        }
        MentorAgent._isListening = false;
    },

    isVoiceSupported: () => {
        return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
    },

    // ── Voice Output (Speech Synthesis) — roadmap #17 ────────────────────────
    //
    // Speaking has to keep pace with the stream. It used to be one `speak()` call after the
    // whole answer had arrived, which meant silence for the entire response and then a
    // read-back — fine for one line, useless for a paragraph.
    //
    // The unit that can be spoken is a *sentence*: a SpeechSynthesisUtterance cannot be
    // appended to once queued, and `cancel()` is the only way to stop one, so a half-sentence
    // cannot be extended when the next chunk lands. Text is therefore buffered until a
    // sentence boundary and queued as its own utterance. The queue belongs to
    // `speechSynthesis` itself — consecutive `speak()` calls play back to back with no
    // audible seam — which is exactly why `cancel()` must never be called between sentences:
    // it clears the whole queue, not the current utterance.
    _isSpeaking: false,
    _speechPending: 0,
    _speechBuffer: '',
    _speechLang: 'en-US',
    _speechRate: 1.0,
    _speechOpen: false,
    _resumeWhenQuiet: false,
    _keepAlive: null,

    beginSpeech: (lang, rate) => {
        if (!window.speechSynthesis) return;
        // Only clear the queue when no session is open. Re-entering an open one must not cancel
        // what it already queued: the caller can race two chunks into this, and the second call
        // used to silence the opening sentence of every answer — which reads as "streaming does
        // not work". A genuine new turn always arrives after endSpeech or a barge-in stop.
        if (!MentorAgent._speechOpen) MentorAgent.stopSpeaking();
        MentorAgent._speechLang = MentorAgent._toLangCode(lang);
        MentorAgent._speechRate = rate && rate > 0 ? rate : 1.0;
        MentorAgent._speechOpen = true;

        // Hands-free: close the microphone now that an answer has actually begun.
        //
        // `onend` decides whether to hold the microphone shut by asking whether the assistant is
        // speaking — but it runs the instant the user stops talking, which is the one moment it
        // never is: the turn has not produced a token yet. So it always took the "reopen in 300 ms"
        // branch, and the microphone was live by the time playback started, transcribing the
        // assistant through the same speakers and sending its own answer back as the next question.
        //
        // This is the missing half of `_resumeWhenQuiet`. Aborting here (not stop(): a half-heard
        // fragment must be discarded, not submitted) lets `onend` re-run its check with
        // `_speechOpen` true, which arms the flag properly — and `_quiet()` reopens the microphone
        // when playback really has finished. Set `_speechOpen` above this, or the check sees false
        // again and we are back where we started.
        if (MentorAgent._handsFree && MentorAgent._isListening && MentorAgent._recognition) {
            const r = MentorAgent._recognition;
            try { (r.abort || r.stop).call(r); } catch { /* already closed */ }
        }
    },

    pushSpeech: (delta) => {
        if (!MentorAgent._speechOpen || !delta) return;
        MentorAgent._speechBuffer += delta;
        MentorAgent._drain(false);
    },

    endSpeech: () => {
        if (!MentorAgent._speechOpen) return;
        MentorAgent._drain(true);
        MentorAgent._speechOpen = false;
        // Nothing was ever queued (an answer with no speakable text, e.g. only a code block):
        // the hands-free loop would otherwise wait for a playback that never happens.
        if (MentorAgent._speechPending === 0) MentorAgent._quiet();
    },

    _drain: (final) => {
        const buf = MentorAgent._speechBuffer;
        if (!buf) return;

        // An odd number of fences means a code block is still arriving. Speaking up to the
        // boundary would read out the first half of the code; waiting lets
        // _cleanTextForSpeech drop the block whole once it closes.
        if (!final && (buf.match(/```/g) || []).length % 2 === 1) return;

        const cut = final ? buf.length - 1 : MentorAgent._lastBoundary(buf);
        if (cut < 0) return;

        MentorAgent._speechBuffer = buf.slice(cut + 1);
        MentorAgent._enqueue(buf.slice(0, cut + 1));
    },

    // Index of the last character that safely ends a sentence, or -1.
    _lastBoundary: (buf) => {
        let cut = -1, inFence = false;
        for (let i = 0; i < buf.length; i++) {
            // A fenced block is full of newlines, and every one of them looks like a sentence
            // boundary. Cutting on one hands _cleanTextForSpeech half a block — and it only
            // strips a block it can see both ends of, so the code gets read out loud instead.
            if (buf[i] === '`' && buf[i + 1] === '`' && buf[i + 2] === '`') {
                inFence = !inFence;
                i += 2;
                continue;
            }
            if (inFence) continue;

            const c = buf[i];
            if (c === '\n') { cut = i; continue; }
            if (c !== '.' && c !== '!' && c !== '?' && c !== '…') continue;
            // "1.234" and "2.5" are one number, not two sentences.
            if (c === '.' && MentorAgent._isDigit(buf[i - 1]) && MentorAgent._isDigit(buf[i + 1])) continue;
            // A terminator at the very end of the buffer is not a boundary yet: the next chunk
            // may continue it ("2." + "5"). endSpeech() flushes the tail.
            const next = buf[i + 1];
            if (next === ' ' || next === '\n') cut = i;
        }
        return cut;
    },

    _isDigit: (c) => c >= '0' && c <= '9',

    _enqueue: (text) => {
        const cleaned = MentorAgent._cleanTextForSpeech(text);
        if (!cleaned) return;

        const u = new SpeechSynthesisUtterance(cleaned);
        u.lang = MentorAgent._speechLang;
        u.rate = MentorAgent._speechRate;
        u.pitch = 1.0;
        u.volume = 1.0;

        u.onstart = () => { MentorAgent._isSpeaking = true; };
        const done = () => {
            MentorAgent._speechPending = Math.max(0, MentorAgent._speechPending - 1);
            if (MentorAgent._speechPending === 0 && !MentorAgent._speechOpen) MentorAgent._quiet();
        };
        u.onend = done;
        u.onerror = done;

        MentorAgent._speechPending++;
        window.speechSynthesis.speak(u);
        MentorAgent._startKeepAlive();
    },

    _quiet: () => {
        MentorAgent._isSpeaking = false;
        MentorAgent._stopKeepAlive();
        if (MentorAgent._resumeWhenQuiet) {
            MentorAgent._resumeWhenQuiet = false;
            if (MentorAgent._handsFree) setTimeout(() => MentorAgent._listen(), 300);
        }
    },

    // Chrome stops synthesis after roughly 15 s unless it is nudged. Sentence-sized utterances
    // make that rare, but a single long sentence is enough to lose the rest of an answer and
    // the failure is silent. The ticker only runs while something is queued.
    _startKeepAlive: () => {
        if (MentorAgent._keepAlive) return;
        MentorAgent._keepAlive = setInterval(() => {
            if (MentorAgent._speechPending === 0) { MentorAgent._stopKeepAlive(); return; }
            window.speechSynthesis.pause();
            window.speechSynthesis.resume();
        }, 10000);
    },

    _stopKeepAlive: () => {
        if (MentorAgent._keepAlive) {
            clearInterval(MentorAgent._keepAlive);
            MentorAgent._keepAlive = null;
        }
    },

    // One-shot path, kept for callers that already have the whole text.
    speak: (text, lang, rate) => {
        if (!window.speechSynthesis) return;
        MentorAgent.beginSpeech(lang, rate);
        MentorAgent.pushSpeech(text);
        MentorAgent.endSpeech();
    },

    stopSpeaking: () => {
        MentorAgent._speechOpen = false;
        MentorAgent._speechBuffer = '';
        MentorAgent._speechPending = 0;
        MentorAgent._resumeWhenQuiet = false;
        MentorAgent._stopKeepAlive();
        if (window.speechSynthesis) window.speechSynthesis.cancel();
        MentorAgent._isSpeaking = false;
    },

    isSpeaking: () => MentorAgent._isSpeaking,

    isSpeechSynthesisSupported: () => !!window.speechSynthesis,

    // ── Image attachments — paste & drag-and-drop (roadmap #13) ──────────────
    //
    // Files are NEVER marshalled to .NET as base64 through a normal interop call: on Blazor
    // Server that would exceed HubOptions.MaximumReceiveMessageSize (32 KB by default) and kill
    // the circuit. Instead the Blob is parked here and .NET pulls the bytes through an
    // IJSStreamReference, which chunks transparently on both Server and WASM.
    _pending: [],

    // Re-encodes an oversized image so it fits under maxBytes instead of being rejected.
    // Screenshots are routinely 5-10 MB; vision models tile them at 512 px anyway, so
    // downscaling costs nothing in answer quality. The original MIME type is preserved so
    // the result still satisfies the server's AllowedImageTypes allow-list.
    _shrinkImage: async (file, maxBytes) => {
        if (!maxBytes || file.size <= maxBytes) return file;
        // Animated GIFs would be flattened by a canvas round-trip — leave them alone and let
        // the size check report a clear error instead of silently destroying the animation.
        if (file.type === 'image/gif' || typeof createImageBitmap !== 'function') return file;

        let bitmap;
        try { bitmap = await createImageBitmap(file); } catch { return file; }

        const type = (file.type === 'image/jpeg' || file.type === 'image/webp') ? file.type : 'image/png';
        const quality = type === 'image/png' ? undefined : 0.85;
        let scale = Math.min(1, 2000 / Math.max(bitmap.width, bitmap.height));

        try {
            for (let attempt = 0; attempt < 6; attempt++) {
                const w = Math.max(1, Math.round(bitmap.width * scale));
                const h = Math.max(1, Math.round(bitmap.height * scale));
                const canvas = document.createElement('canvas');
                canvas.width = w; canvas.height = h;
                const ctx = canvas.getContext('2d');
                // A transparent PNG flattened onto white keeps screenshots readable.
                ctx.fillStyle = '#fff';
                ctx.fillRect(0, 0, w, h);
                ctx.drawImage(bitmap, 0, 0, w, h);

                const blob = await new Promise(res => canvas.toBlob(res, type, quality));
                if (blob && blob.size <= maxBytes)
                    return new File([blob], file.name || 'image', { type });
                scale *= 0.7;
            }
        } catch { /* fall through — .NET reports "too large" */ }
        finally { bitmap.close && bitmap.close(); }
        return file;
    },

    attachInit: (dotnetRef, el, maxBytes) => {
        if (!el || el._bmAttach) return;

        // Drag & drop is accepted anywhere in the chat panel, not just on the composer.
        const zone = el.closest('.bm-panel') || el;
        const root = el.closest('.bm-root');
        const isOpen = () => !root || root.classList.contains('bm-open');

        const push = async (files) => {
            let images = Array.from(files || []).filter(f => f && f.type && f.type.startsWith('image/'));
            if (images.length === 0) return;
            images = await Promise.all(images.map(f => MentorAgent._shrinkImage(f, maxBytes)));
            for (const f of images) MentorAgent._pending.push(f);
            await dotnetRef.invokeMethodAsync('OnFilesPending');
        };

        // Paste is bound to the DOCUMENT, not to the composer: a paste event is delivered to
        // whatever currently has focus, so binding it to the input area meant Ctrl+V did
        // nothing at all (silently) unless the user had clicked into the textarea first.
        const onPaste = (e) => {
            if (!isOpen()) return;

            // Never hijack a paste aimed at another editable field of the host app.
            const t = e.target;
            if (t && !zone.contains(t)) {
                const tag = (t.tagName || '').toUpperCase();
                if (tag === 'INPUT' || tag === 'TEXTAREA' || t.isContentEditable) return;
            }

            const items = (e.clipboardData && e.clipboardData.items) || [];
            const files = [];
            for (const item of items) {
                if (item.kind === 'file') {
                    const f = item.getAsFile();
                    if (f && f.type && f.type.startsWith('image/')) files.push(f);
                }
            }
            if (files.length > 0) { e.preventDefault(); push(files); }
        };

        const onDragOver = (e) => { e.preventDefault(); e.stopPropagation(); };
        const onDragEnter = (e) => {
            e.preventDefault(); e.stopPropagation();
            zone._bmDepth = (zone._bmDepth || 0) + 1;
            if (zone._bmDepth === 1) dotnetRef.invokeMethodAsync('OnDragStateChanged', true);
        };
        const onDragLeave = (e) => {
            e.preventDefault(); e.stopPropagation();
            zone._bmDepth = Math.max(0, (zone._bmDepth || 0) - 1);
            if (zone._bmDepth === 0) dotnetRef.invokeMethodAsync('OnDragStateChanged', false);
        };
        const onDrop = async (e) => {
            e.preventDefault(); e.stopPropagation();
            zone._bmDepth = 0;
            dotnetRef.invokeMethodAsync('OnDragStateChanged', false);

            const dt = e.dataTransfer;
            if (!dt) return;
            if (dt.files && dt.files.length > 0) { await push(dt.files); return; }

            // Dragging an <img> from another page yields a URL rather than a file.
            const url = (dt.getData('text/uri-list') || dt.getData('text/plain') || '').trim();
            if (!url) return;

            // blob:/data: URLs are local to this document — fetch the bytes and treat it as a file,
            // otherwise the drop would be silently ignored (the server could never resolve them).
            if (/^(blob:|data:)/i.test(url)) {
                try {
                    const blob = await (await fetch(url)).blob();
                    if (blob.type && blob.type.startsWith('image/'))
                        await push([new File([blob], 'dropped', { type: blob.type })]);
                } catch { /* ignore — nothing we can attach */ }
                return;
            }
            if (/^https?:\/\//i.test(url))
                await dotnetRef.invokeMethodAsync('OnUrlDropped', url);
        };

        document.addEventListener('paste', onPaste);
        zone.addEventListener('dragover', onDragOver);
        zone.addEventListener('dragenter', onDragEnter);
        zone.addEventListener('dragleave', onDragLeave);
        zone.addEventListener('drop', onDrop);
        el._bmAttach = { zone, onPaste, onDragOver, onDragEnter, onDragLeave, onDrop };
    },

    attachDispose: (el) => {
        if (!el || !el._bmAttach) return;
        const h = el._bmAttach;
        document.removeEventListener('paste', h.onPaste);
        h.zone.removeEventListener('dragover', h.onDragOver);
        h.zone.removeEventListener('dragenter', h.onDragEnter);
        h.zone.removeEventListener('dragleave', h.onDragLeave);
        h.zone.removeEventListener('drop', h.onDrop);
        el._bmAttach = null;
        MentorAgent._pending = [];
    },

    // Metadata for every parked file — .NET validates against the allow-list before pulling bytes.
    attachTakeMeta: () => MentorAgent._pending.map(f => ({
        mimeType: f.type || '',
        fileName: f.name || null,
        size: f.size || 0
    })),

    // The raw Blob for one parked file. Return it as-is: when .NET declares the return type
    // as IJSStreamReference, Blazor calls DotNet.createJSStreamReference() on this value
    // itself — wrapping it here would hand Blazor a plain object and throw
    // "Supplied value is not a typed array or blob."
    attachTakeData: (index) => MentorAgent._pending[index] ?? null,

    attachClearPending: () => { MentorAgent._pending = []; },

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Converte lingua MentorAgent (es. "Italian", "it") → codice BCP-47 (es. "it-IT")
    _toLangCode: (lang) => {
        if (!lang) return 'en-US';
        const map = {
            'it': 'it-IT', 'italian': 'it-IT', 'italiano': 'it-IT',
            'en': 'en-US', 'english': 'en-US', 'inglese': 'en-US',
            'fr': 'fr-FR', 'french': 'fr-FR', 'francese': 'fr-FR',
            'de': 'de-DE', 'german': 'de-DE', 'tedesco': 'de-DE',
            'es': 'es-ES', 'spanish': 'es-ES', 'spagnolo': 'es-ES',
            'pt': 'pt-PT', 'portuguese': 'pt-PT', 'portoghese': 'pt-PT',
            'nl': 'nl-NL', 'dutch': 'nl-NL',
            'pl': 'pl-PL', 'polish': 'pl-PL',
            'ja': 'ja-JP', 'japanese': 'ja-JP',
            'zh': 'zh-CN', 'chinese': 'zh-CN',
        };
        const key = lang.toLowerCase().trim();
        // Prova match diretto
        if (map[key]) return map[key];
        // Prova prime 2 lettere (es. "it-IT" → "it")
        const short = key.substring(0, 2);
        return map[short] || lang;
    },

    // Rimuove markdown, codice, emoji prima di leggere ad alta voce
    _cleanTextForSpeech: (text) => {
        if (!text) return '';
        return text
            // Rimuove blocchi di codice ```...```
            .replace(/```[\s\S]*?```/g, ' ')
            // Rimuove codice inline `...`
            .replace(/`[^`]+`/g, ' ')
            // Tabelle (#14 L1): la riga separatore |---|---| non si legge, e le pipe
            // diventano pause. Senza questo una tabella viene scandita come
            // "pipe prodotto pipe prezzo pipe".
            .replace(/^\s*\|?[\s:-]*\|[\s|:-]*$/gm, ' ')
            .replace(/\s*\|\s*/g, ', ')
            // Le pipe di apertura e chiusura riga lasciano virgole vuote: ", A, B, , 1, 2,".
            .replace(/,\s*(?=,)/g, '')
            .replace(/(^|\n)\s*,\s*/g, '$1')
            .replace(/\s*,\s*$/gm, '')
            // Rimuove markdown bold/italic **..** *..* __..__ _
            .replace(/(\*\*|__)(.*?)\1/g, '$2')
            .replace(/(\*|_)(.*?)\1/g, '$2')
            // Rimuove link [text](url)
            .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
            // Rimuove emoji (range unicode)
            .replace(/[\u{1F000}-\u{1FFFF}]/gu, '')
            .replace(/[\u{2600}-\u{27BF}]/gu, '')
            // Rimuove simboli #, >, -, * a inizio riga (markdown)
            .replace(/^[#>\-\*]+\s*/gm, '')
            // Collassa spazi multipli
            .replace(/\s+/g, ' ')
            .trim();
    }
};

// Two <script> tags for this file is a supported configuration, not a mistake: a standalone
// WebAssembly app loads index.html before .NET starts, so the documented setup puts a versioned tag
// there to defeat the browser cache, while the widget's own <HeadContent> injects a second one once
// it renders. The second load therefore lands *after* the first has been running, and because
// everything above is one object literal it would otherwise reset the voice state machine —
// dropping `_handsFree`, `_isSpeaking`, `_speechOpen` and, worst, the live SpeechRecognition handle
// in `_recognition`, which then has no owner and no way to be stopped.
//
// Carrying the runtime state across keeps the newest code (the later tag wins, which is the whole
// point of the version query) without taking a live session down with it. Only mutable state is
// carried; the functions come from the new definition every time.
if (__mentorAgentPrevious) {
    for (const key of ['_recognition', '_handsFree', '_isSpeaking', '_speechOpen', '_resumeWhenQuiet'])
        if (key in __mentorAgentPrevious) window.MentorAgent[key] = __mentorAgentPrevious[key];
}
