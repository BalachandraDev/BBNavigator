import { h, clear, fill, renderAnswer, toast, confirmDialog } from '../dom.js';
import { api, session, describeSession } from '../api.js';
import { icon, typewrite, formatTime, relativeTime, greeting, copyText } from '../chatkit.js';

const NAME = 'BBNavigator Assistant';
const SUGGESTIONS = [
  ['Validations', 'What validations apply on the registration page?'],
  ['Roles & access', 'Which roles can approve or edit records?'],
  ['Workflows', 'Walk me through the main workflow step by step.'],
  ['Business rules', 'What business rules should I know about?'],
];

export async function chatView(main, ctx) {
  let conversationId = null;
  let sending = false;
  let alive = true;
  let stick = true;               // keep the newest message in view unless the user scrolled up
  let isWelcome = true;
  let typing = null;              // the answer currently being "typed", so it can be completed instantly
  ctx.onCleanup(() => { alive = false; if (typing) typing.finish(); });

  // ---- building blocks -------------------------------------------------------------------------------------------
  const avatar = role => h('div', { class: 'avatar ' + role, 'aria-hidden': 'true' }, icon(role === 'assistant' ? 'sparkle' : 'user', 18));

  function copyButton(text) {
    const b = h('button', { class: 'chip-btn', type: 'button', 'aria-label': 'Copy answer', title: 'Copy answer' }, icon('copy', 14), h('span', null, 'Copy'));
    b.addEventListener('click', async () => {
      if (!await copyText(text.replace(/\s*\[\d{1,2}\]/g, ''))) { toast('Select the text and copy it manually.', 'info'); return; }
      fill(b, icon('check', 14), h('span', null, 'Copied')); setTimeout(() => fill(b, icon('copy', 14), h('span', null, 'Copy')), 1600);
    });
    return b;
  }

  function userRow(text, iso) {
    return h('div', { class: 'row user' },
      h('div', { class: 'col' }, h('div', { class: 'bubble' }, text), h('div', { class: 'meta' }, formatTime(iso))), avatar('user'));
  }

  /// A long message is condensed to its key question before it is sent to the AI; show the user what was understood.
  function understoodNote(mine, question) {
    const col = mine.querySelector('.col');
    if (col && question) col.append(h('div', { class: 'understood' }, icon('info', 13), h('span', null, 'Understood as: ', h('em', null, question))));
  }

  function notFound(text) {
    return h('div', { class: 'nf' }, icon('info', 20),
      h('div', null, h('strong', null, text || "I couldn't find that in the approved documentation."),
        h('p', null, 'Try rephrasing your question, or type "what can I ask?" to see the topics available to you. Your administrator can add more.')));
  }
  // Ordinary users can pass an unanswered question to their administrator. (An admin would only be sending it to themselves.)
  const canAskSupport = describeSession(session.get() || {}).role === 'TenantUser';
  function supportButton(button) {
    const b = h('button', { class: 'chip-btn', type: 'button', title: 'Ask your administrator to help with this question' }, icon('send', 14), h('span', null, 'Send to support'));
    b.addEventListener('click', () => supportDialog(b));
    return b;
  }
  function supportDialog(button) {
    const note = h('textarea', { id: 'sup-note', rows: 3, maxlength: 500, placeholder: 'e.g. I need this for a patient discharge today' });
    const contact = h('input', { id: 'sup-contact', type: 'text', maxlength: 120, autocomplete: 'off', placeholder: 'Name, extension or phone' });
    const err = h('p', { class: 'error-text', role: 'alert' });
    const go = h('button', { class: 'btn btn-primary', type: 'submit' }, 'Send');
    const dlg = h('dialog', { class: 'dialog', 'aria-label': 'Send to support' });
    fill(dlg, h('form', {
      method: 'dialog', onsubmit: async e => {
        e.preventDefault(); go.disabled = true; err.textContent = '';
        try {
          await api('POST', '/chat/support', { body: { conversationId, note: note.value, contact: contact.value } });
          dlg.close(); toast('Sent to your administrator.', 'ok');
          button.replaceWith(h('span', { class: 'muted small' }, 'Sent to support'));
        } catch (ex) { err.textContent = ex.message; go.disabled = false; }
      },
    },
      h('div', { class: 'dlg-head' }, h('h2', null, 'Send to support'),
        h('button', { class: 'btn btn-sm icon-btn', type: 'button', 'aria-label': 'Close', title: 'Close', onclick: () => dlg.close() }, icon('close', 16))),
      h('p', null, 'Your administrator will see your last question and can add the missing information.'),
      h('div', { class: 'field' }, h('label', { for: 'sup-note' }, 'What are you trying to do? (optional)'), note),
      h('div', { class: 'field' }, h('label', { for: 'sup-contact' }, 'How can they reach you? (optional)'), contact),
      err,
      h('div', { class: 'actions' }, h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Cancel'), go)));
    dlg.addEventListener('close', () => dlg.remove());
    document.body.append(dlg); dlg.showModal(); note.focus();
  }

  /** A reply from the assistant. animate=true types it out; the actions appear when it finishes. */
  function assistantRow(m, { animate = false, plain = false } = {}) {
    const documented = m.documented !== false;
    const content = documented ? renderAnswer(m.content) : notFound(m.content);   // no citation numbers and no source names are ever shown
    const bubble = h('div', { class: 'bubble' + (documented ? '' : ' notfound') }, content);
    const meta = plain ? null : h('div', { class: 'meta' }, formatTime(m.time), documented ? copyButton(m.content) : null, !documented && m.askSupport && canAskSupport ? supportButton() : null);
    const footer = h('div', { class: 'footer' + (animate ? ' pending' : '') }, meta);
    const row = h('div', { class: 'row assistant' }, avatar('assistant'), h('div', { class: 'col' }, bubble, footer));

    if (animate) {
      const untyped = content.querySelector('[data-notype]');      // shown only once the sentence has finished typing
      if (untyped) untyped.hidden = true;
      let completed = false;     // typewrite finishes synchronously when motion is reduced, before it returns
      const tw = typewrite(content, {
        onTick: scrollIfStuck,
        onDone: () => { completed = true; if (untyped) untyped.hidden = false; footer.classList.remove('pending'); scrollIfStuck(); if (typing && typing.owner === row) typing = null; },
      });
      if (!completed) { tw.owner = row; typing = tw; }
      bubble.addEventListener('click', () => tw.finish());     // click to skip the animation
    }
    return row;
  }

  function typingRow() {
    return h('div', { class: 'row assistant', role: 'status' }, avatar('assistant'),
      h('div', { class: 'col' }, h('div', { class: 'bubble typing' }, h('span', { class: 'dot' }), h('span', { class: 'dot' }), h('span', { class: 'dot' }),
        h('span', { class: 'sr-only' }, NAME + ' is typing'))));
  }

  function errorRow(e, retryText, userRowEl) {
    const retry = h('button', { class: 'chip-btn', type: 'button' }, icon('refresh', 14), h('span', null, 'Try again'));
    const row = h('div', { class: 'row assistant', role: 'alert' }, avatar('assistant'),
      h('div', { class: 'col' }, h('div', { class: 'bubble error' }, icon('alert', 18),
        h('div', null, h('strong', null, "I couldn't answer that just now."), h('p', null, friendlyError(e)))),
      h('div', { class: 'footer' }, retry)));
    retry.addEventListener('click', () => { row.remove(); if (userRowEl) userRowEl.remove(); submit(retryText); });
    return row;
  }

  function friendlyError(e) {
    if (e.status === 429) return e.message + ' Nothing paid is used in the meantime.';
    return e.message || 'Something went wrong. Please try again.';
  }

  // ---- scrolling -------------------------------------------------------------------------------------------------
  const thread = h('div', { class: 'thread', role: 'log', 'aria-live': 'polite', 'aria-label': 'Conversation with ' + NAME });
  const toBottom = h('button', { class: 'to-bottom', type: 'button', hidden: true, 'aria-label': 'Scroll to latest message', onclick: () => scrollDown(true) }, icon('down', 18));
  function nearBottom() { return thread.scrollHeight - thread.scrollTop - thread.clientHeight < 80; }
  function scrollIfStuck() { if (stick && !isWelcome) thread.scrollTop = thread.scrollHeight; }   // the greeting is never auto-scrolled
  function scrollDown(force) { if (force) stick = true; thread.scrollTop = thread.scrollHeight; toBottom.hidden = true; }
  thread.addEventListener('scroll', () => { stick = nearBottom(); toBottom.hidden = stick; });

  // ---- reply language: Automatic follows the question; English / Tamil forces the answer language ----
  let replyLang = 'auto';
  try { const v = localStorage.getItem('flowmind.chatlang'); if (v === 'en' || v === 'ta') replyLang = v; } catch { /* default */ }
  const langBtns = [['Auto', 'auto'], ['English', 'en'], ['தமிழ்', 'ta']].map(([label, v]) => {
    const b = h('button', { type: 'button', role: 'radio', onclick: () => { replyLang = v; try { localStorage.setItem('flowmind.chatlang', v); } catch { /* not remembered */ } paintLang(); } }, label); b.dataset.v = v; return b;
  });
  const langPick = h('div', { class: 'choice mini', role: 'radiogroup', 'aria-label': 'Reply language', title: 'Language of the answers' }, langBtns);
  function paintLang() { langBtns.forEach(b => b.setAttribute('aria-checked', String(b.dataset.v === replyLang))); }
  paintLang();

  // ---- composer --------------------------------------------------------------------------------------------------
  const input = h('textarea', { id: 'msg', rows: 1, maxlength: 8000, placeholder: 'Message ' + NAME + '…', 'aria-label': 'Your message' });
  const counter = h('span', { class: 'counter' });
  const send = h('button', { class: 'send', type: 'button', 'aria-label': 'Send message', onclick: () => submit() }, icon('send', 18));
  function resize() {
    input.style.height = 'auto'; input.style.height = Math.min(input.scrollHeight, 160) + 'px';
    counter.textContent = input.value.length > 6000 ? `${input.value.length}/8000` : '';
    send.disabled = sending || !input.value.trim();
  }
  input.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); submit(); } });
  input.addEventListener('input', resize);

  // ---- conversation flow -----------------------------------------------------------------------------------------
  function finishTyping() { if (typing) { typing.finish(); typing = null; } }

  async function submit(textArg) {
    const text = (textArg ?? input.value).trim();
    if (!text || sending) return;
    finishTyping();
    sending = true; input.value = ''; resize();
    if (isWelcome) { clear(thread); isWelcome = false; }

    const mine = userRow(text, new Date().toISOString());
    const waiting = typingRow();
    thread.append(mine, waiting); scrollDown(true);
    try {
      const r = await api('POST', '/chat', { body: { applicationId: ctx.appId, conversationId, message: text, ...(replyLang !== 'auto' ? { language: replyLang } : {}) } });
      if (!alive) return;
      const isNew = conversationId !== r.conversationId;
      conversationId = r.conversationId;
      understoodNote(mine, r.understood);
      thread.querySelectorAll('.chip-btn[title^="Ask your administrator"]').forEach(b => b.remove());   // the server sends the newest question only
      waiting.replaceWith(assistantRow({ content: r.answer, documented: r.documented, time: new Date().toISOString(), askSupport: true }, { animate: true }));
      if (isNew) loadConversations();
    } catch (e) {
      if (!alive) return;
      waiting.replaceWith(errorRow(e, text, mine));
    } finally { sending = false; resize(); if (alive) input.focus(); scrollIfStuck(); }
  }

  function welcome() {
    finishTyping(); isWelcome = true; conversationId = null; clear(thread);
    const text = `${greeting()}! I'm ${NAME}. Ask me anything about ${ctx.app?.name || 'your application'}. I answer only from its approved documentation, and I always show you where each answer comes from.`;
    const hello = assistantRow({ content: text, documented: true, time: new Date().toISOString() }, { animate: true, plain: true });
    const cards = h('div', { class: 'suggestions' }, SUGGESTIONS.map(([title, q]) =>
      h('button', { class: 'suggest', type: 'button', onclick: () => submit(q) }, h('strong', null, title), h('span', null, q))));
    thread.append(hello, cards);
    // The greeting is read from the top, not pinned to the bottom. Jump instantly: smooth scrolling would fire scroll
    // events mid-animation that switch auto-follow back on.
    stick = false; thread.style.scrollBehavior = 'auto'; thread.scrollTop = 0; thread.style.scrollBehavior = ''; toBottom.hidden = true;
  }

  async function open(id) {
    try {
      const c = await api('GET', '/conversations/' + id);
      finishTyping(); isWelcome = false; conversationId = c.id; clear(thread);
      c.messages.forEach(m => thread.append(m.role === 'user' ? userRow(m.content, m.createdUtc)
        : assistantRow({ content: m.content, documented: m.documented, time: m.createdUtc })));
      side.classList.remove('open');
      loadConversations(); scrollDown(true);
    } catch (e) { toast(e.message, 'bad'); }
  }

  // ---- conversation list -----------------------------------------------------------------------------------------
  const convoList = h('ul', { class: 'convos', 'aria-label': 'Previous conversations' });
  async function loadConversations() {
    let list = [];
    try { list = await api('GET', '/conversations?applicationId=' + encodeURIComponent(ctx.appId)); } catch (e) { toast(e.message, 'bad'); }
    if (!alive) return;
    clear(convoList);
    if (!list.length) convoList.append(h('li', { class: 'muted small empty-li' }, 'Your conversations will appear here.'));
    list.forEach(c => convoList.append(h('li', { 'aria-current': String(c.id === conversationId) },
      h('button', { class: 'pick', type: 'button', title: c.title, onclick: () => open(c.id) },
        h('span', { class: 'ttl' }, c.title || 'Conversation'), h('span', { class: 'when' }, relativeTime(c.updatedUtc))),
      h('button', { class: 'del', type: 'button', 'aria-label': 'Delete conversation ' + c.title, onclick: () => remove(c) }, icon('trash', 15)))));
  }
  async function remove(c) {
    if (!await confirmDialog(`Delete the conversation "${c.title}"?`)) return;
    try { await api('DELETE', '/conversations/' + c.id); if (conversationId === c.id) welcome(); await loadConversations(); } catch (e) { toast(e.message, 'bad'); }
  }

  // ---- layout ----------------------------------------------------------------------------------------------------
  const side = h('aside', { class: 'chat-side', 'aria-label': 'Conversations' },
    h('button', { class: 'btn btn-primary newchat', type: 'button', onclick: () => { welcome(); loadConversations(); side.classList.remove('open'); input.focus(); } }, icon('plus', 16), 'New chat'),
    h('h3', { class: 'side-title' }, 'Recent'), convoList);

  const header = h('header', { class: 'chat-head' },
    h('button', { class: 'icon-btn side-toggle', type: 'button', 'aria-label': 'Show conversations', onclick: () => side.classList.toggle('open') }, icon('menu', 20)),
    h('div', { class: 'avatar assistant lg', 'aria-hidden': 'true' }, icon('sparkle', 22)),
    h('div', { class: 'who-is' }, h('strong', null, NAME), h('span', { class: 'status' }, h('i', { class: 'dot-online' }), 'Online · answers from approved documentation')),
    langPick,
    ctx.app ? h('span', { class: 'app-pill', title: 'Application' }, ctx.app.name) : null);

  const composer = h('div', { class: 'composer-wrap' },
    h('div', { class: 'composer' }, input, counter, send),
    h('p', { class: 'disclaimer' }, 'Enter to send · Shift+Enter for a new line. Answers are taken from approved documents only. Please double-check important steps before acting on them.'));

  main.append(h('div', { class: 'chat-shell' }, side,
    h('section', { class: 'chat-panel', 'aria-label': 'Chat with ' + NAME }, header, h('div', { class: 'thread-wrap' }, thread, toBottom), composer)));

  resize();
  welcome();
  await loadConversations();
  input.focus();
}
