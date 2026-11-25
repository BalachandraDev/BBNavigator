// Interface text for the public chat page in English and Tamil. Questions and answers are separate: the server answers in the language the
// visitor writes in. This only translates the page itself (buttons, hints, messages).
const KEY = 'flowmind.lang';

const DICT = {
  en: {
    langSwitch: 'தமிழ்', langSwitchLabel: 'Switch to Tamil',
    goodMorning: 'Good morning', goodAfternoon: 'Good afternoon', goodEvening: 'Good evening',
    online: 'Online', newChat: 'New chat', startNewChat: 'Start a new chat', history: 'Your earlier chats',
    placeholder: 'Ask a question…', yourQuestion: 'Your question', send: 'Send', toLatest: 'Scroll to latest', typing: 'Typing',
    footer: 'Answers are limited to the topics set up for this assistant. Double-check important steps.',
    welcome: title => `I'm ${title}. Ask me anything about it and I'll help.`,
    copy: 'Copy', copied: 'Copied', pressCtrlC: 'Press Ctrl+C', tryAgain: 'Try again',
    notFound: "I couldn't find that. Try rephrasing your question, or ask about a topic I cover.",
    yourChats: 'Your chats', clearAll: 'Clear all', close: 'Close', noChats: 'No earlier chats yet.', deleteChat: 'Delete this chat',
    sheetNote: days => `Only you can see these, on this device. They are removed after ${days} days, or sooner if you delete them.`,
    confirmClear: 'Delete all your chats on this assistant? This cannot be undone.', deleteAll: 'Delete all',
    toLight: 'Switch to light mode', toDark: 'Switch to dark mode',
    unavailableTitle: 'This assistant is not available', incomplete: 'This link is incomplete. Ask the person who shared it for the full link.',
    inactive: 'This link is no longer active. Ask the person who shared it for a new one.', tryLater: 'Please try again in a moment.',
    errRate: null, errUnavailable: null, errGeneric: null, errStale: 'That conversation was not found. Start a new chat.',
    askTeam: 'Send this to our team', supportTitle: 'Send your question to our team', supportIntro: 'A person on our team will see this question:',
    supportNote: 'What were you trying to do? (optional)', supportContact: 'How can we reach you? (optional)',
    supportContactHint: 'Your name, phone or email. Only our team sees it.', supportSend: 'Send', supportSent: 'Sent to our team. Thank you.', cancel: 'Cancel',
  },
  ta: {
    langSwitch: 'English', langSwitchLabel: 'ஆங்கிலத்திற்கு மாறு',
    goodMorning: 'காலை வணக்கம்', goodAfternoon: 'மதிய வணக்கம்', goodEvening: 'மாலை வணக்கம்',
    online: 'இணைப்பில்', newChat: 'புதிய உரையாடல்', startNewChat: 'புதிய உரையாடலைத் தொடங்கு', history: 'முந்தைய உரையாடல்கள்',
    placeholder: 'உங்கள் கேள்வியை உள்ளிடுங்கள்…', yourQuestion: 'உங்கள் கேள்வி', send: 'அனுப்பு', toLatest: 'சமீபத்தியதற்குச் செல்', typing: 'தட்டச்சு செய்கிறது',
    footer: 'பதில்கள் இந்த உதவியாளருக்காக அமைக்கப்பட்ட தலைப்புகளுக்கு மட்டுமே. முக்கியமான படிகளை இருமுறை சரிபார்க்கவும்.',
    welcome: title => `நான் ${title}. இதைப் பற்றி எதையும் கேளுங்கள், உதவுகிறேன்.`,
    copy: 'நகலெடு', copied: 'நகலெடுக்கப்பட்டது', pressCtrlC: 'Ctrl+C அழுத்தவும்', tryAgain: 'மீண்டும் முயற்சி',
    notFound: 'இது கிடைக்கவில்லை. கேள்வியை வேறு விதமாகக் கேளுங்கள், அல்லது நான் உதவும் தலைப்பைப் பற்றிக் கேளுங்கள்.',
    yourChats: 'உங்கள் உரையாடல்கள்', clearAll: 'அனைத்தையும் அழி', close: 'மூடு', noChats: 'முந்தைய உரையாடல்கள் இல்லை.', deleteChat: 'இந்த உரையாடலை நீக்கு',
    sheetNote: days => `இவற்றை நீங்கள் மட்டுமே, இந்தச் சாதனத்தில் பார்க்க முடியும். ${days} நாட்களுக்குப் பிறகு, அல்லது நீங்கள் நீக்கினால் உடனே அகற்றப்படும்.`,
    confirmClear: 'இந்த உதவியாளரில் உள்ள உங்கள் அனைத்து உரையாடல்களையும் நீக்கவா? இதைத் திரும்பப் பெற முடியாது.', deleteAll: 'அனைத்தையும் நீக்கு',
    toLight: 'ஒளி பயன்முறைக்கு மாறு', toDark: 'இருள் பயன்முறைக்கு மாறு',
    unavailableTitle: 'இந்த உதவியாளர் தற்போது கிடைக்கவில்லை', incomplete: 'இந்த இணைப்பு முழுமையாக இல்லை. இணைப்பைப் பகிர்ந்தவரிடம் முழு இணைப்பைக் கேளுங்கள்.',
    inactive: 'இந்த இணைப்பு இப்போது செயலில் இல்லை. இணைப்பைப் பகிர்ந்தவரிடம் புதிய இணைப்பைக் கேளுங்கள்.', tryLater: 'சிறிது நேரத்தில் மீண்டும் முயலுங்கள்.',
    errRate: 'இப்போது கேள்விகள் அதிகமாக உள்ளன, அல்லது இன்றைய வரம்பு முடிந்துவிட்டது. சிறிது நேரம் கழித்து மீண்டும் முயலுங்கள்.',
    errUnavailable: 'உதவியாளர் தற்போது கிடைக்கவில்லை. சிறிது நேரம் கழித்து முயலுங்கள்.', errGeneric: 'ஏதோ தவறு நடந்துவிட்டது. மீண்டும் முயலுங்கள்.',
    errStale: 'அந்த உரையாடல் கிடைக்கவில்லை. புதிய உரையாடலைத் தொடங்குங்கள்.',
    askTeam: 'இதை எங்கள் குழுவுக்கு அனுப்பு', supportTitle: 'உங்கள் கேள்வியை எங்கள் குழுவுக்கு அனுப்புங்கள்', supportIntro: 'எங்கள் குழுவில் ஒருவர் இந்தக் கேள்வியைப் பார்ப்பார்:',
    supportNote: 'நீங்கள் என்ன செய்ய முயன்றீர்கள்? (விருப்பம்)', supportContact: 'உங்களை எப்படித் தொடர்பு கொள்வது? (விருப்பம்)',
    supportContactHint: 'உங்கள் பெயர், தொலைபேசி அல்லது மின்னஞ்சல். எங்கள் குழு மட்டுமே இதைப் பார்க்கும்.', supportSend: 'அனுப்பு', supportSent: 'எங்கள் குழுவுக்கு அனுப்பப்பட்டது. நன்றி.', cancel: 'ரத்து செய்',
  },
};

let current = 'en';
const stored = () => { try { const v = localStorage.getItem(KEY); return v in DICT ? v : null; } catch { return null; } };

/** The visitor's own choice wins; then the admin's default for this link; then the browser's language. */
export function pickLanguage(adminDefault, ignoreStored = false) {
  const own = ignoreStored ? null : stored();
  if (own) return own;
  if (adminDefault in DICT) return adminDefault;
  if (ignoreStored) return 'en';
  try { if ((navigator.languages || [navigator.language || '']).some(l => String(l).toLowerCase().startsWith('ta'))) return 'ta'; } catch { /* default below */ }
  return 'en';
}

export const lang = () => current;
export function setLang(l, remember = false) {
  current = l in DICT ? l : 'en';
  document.documentElement.lang = current;
  if (remember) { try { localStorage.setItem(KEY, current); } catch { /* not remembered */ } }
}

/** Text for the current language (falls back to English). Entries that are functions take arguments. */
export function t(key, ...args) {
  const v = DICT[current][key] ?? DICT.en[key];
  return typeof v === 'function' ? v(...args) : v;
}

/** The visitor-facing message for a failed call. English keeps the server's own words; Tamil uses a fixed translation by kind of failure. */
export function failure(status, serverMessage) {
  if (current !== 'ta') return serverMessage || 'Something went wrong.';
  if (status === 429) return DICT.ta.errRate;
  if (status === 503) return DICT.ta.errUnavailable;
  if (status === 404) return DICT.ta.errStale;
  return DICT.ta.errGeneric;
}

export const timeGreeting = () => { const h = new Date().getHours(); return t(h < 12 ? 'goodMorning' : h < 18 ? 'goodAfternoon' : 'goodEvening'); };
