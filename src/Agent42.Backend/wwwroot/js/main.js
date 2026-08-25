/* Agent 42 — composition root. The ONLY module nobody imports, and the only caller of the
   per-feature wire*()/init*() entry points (ADR 0041).

   A wire*() that exists but is never called from here loads fine, throws nothing, and silently
   attaches no listeners — that feature's buttons just do nothing, and no build, lint or type check
   sees it. That is the frontend analogue of ADR 0040's composition-root failure mode, so the call
   list below is asserted by name in ProjectLayoutTests. */

import { agents, loadAgents, renderAgents, setAgents, setIncognito, wireAgents } from "./agents.js";
import { wireAttachments } from "./attachments.js";
import { cacheGet } from "./cache.js";
import { wireComposer } from "./composer.js";
import { wireConfigHealth } from "./confighealth.js";
import { activeAgentId, activeId, wireConversations } from "./conversations.js";
import { incognitoBtn, input, toolsBtn } from "./dom.js";
import { wireDrawer } from "./drawer.js";
import { initLocation } from "./location.js";
import { initMcpOAuthReturn, wireMcp } from "./mcp.js";
import { loadModels } from "./models.js";
import { profile, wireProfile } from "./profile.js";
import { loadProviders, wireProviders } from "./providers.js";
import { initPush } from "./push.js";
import { wirePwa } from "./pwa.js";
import { wireSend } from "./send.js";
import { setTopbarShare, wireShareLinks } from "./sharelinks.js";
import { initShare } from "./sharetarget.js";
import { conversations, refreshConversations, renderSidebar, setConversations } from "./sidebar.js";
import { wireSkills } from "./skills.js";
import { wireSpeech } from "./speech.js";
import { initTheme } from "./theme.js";
import { allToolKeys, loadTools, setChatTools, wireTools } from "./tools.js";
import { wireUsage } from "./usage.js";
import { wireVoice } from "./voice.js";

// Paint the last-known agents + conversations from the cache at startup, so an offline (or slow)
// load shows the user's data before—or instead of—the network fetch resolving. The fire-and-forget
// loaders below overwrite both the arrays and the cache when the network succeeds; the length
// guards keep a cache paint from clobbering data the network already delivered (fast path wins).
async function hydrateFromCache() {
  const [cachedConvs, cachedAgents] = await Promise.all([
    cacheGet("conversations"),
    cacheGet("agents"),
  ]);
  if (cachedConvs && conversations.length === 0) {
    setConversations(cachedConvs);
    renderSidebar();
  }
  if (cachedAgents && agents.length === 0) {
    setAgents(cachedAgents);
    renderAgents();
  }
}

/* ————— wiring ————— */

wireShareLinks();
wireAgents();
wireAttachments();
wireConversations();
wireDrawer();
wireSend();
wireComposer();
wireProfile();
wireTools();
wireProviders();
wireConfigHealth();
wireMcp();
wireSkills();
wireUsage();
wireSpeech();
wireVoice();
wirePwa();

/* ————— init ————— */

loadModels();
loadProviders(); // also sets userDefaultEmbeddingModel at startup, so the composer's document-attach gate works
hydrateFromCache(); // paint cached agents + conversations first, so offline (or slow) loads aren't empty (ADR 0032)
refreshConversations();
loadAgents();
const profileReady = initTheme(); // loads the profile and reconciles the theme (server wins over the pre-paint cache)
const toolsReady = loadTools();
// The initial state is a fresh regular chat, so the per-chat tools button is available (ADR 0021),
// and incognito can be chosen — off and interactive until the first send (ADR 0034).
toolsBtn.hidden = false;
incognitoBtn.hidden = false;
setIncognito(false, true);
// Same reason the tool seed below is needed: an empty boot chat has nothing to expose, and a link minted
// here would resolve to nothing.
setTopbarShare(null);
// The boot chat isn't created through startNewConversation(), so seed its composer tool selection
// from the user default once BOTH the profile (enabledTools) and the catalog (allToolKeys) have
// loaded — otherwise the Tools popover shows an empty set on startup instead of the saved default
// (ADR 0021). Guard on the chat still being the untouched boot chat with no agent, so a share
// hand-off (initShare → startNewConversation) or an opened conversation (selectConversation, which
// sets chatTools from the server) wins the race instead of being clobbered. null default = all.
const bootChatId = activeId;
Promise.all([profileReady, toolsReady]).then(() => {
  if (activeId === bootChatId && activeAgentId === null) {
    setChatTools(profile.enabledTools ?? allToolKeys());
  }
});
initLocation();
initPush(); // wires the push toggle + the SW open-conversation message handler (ADR 0028)
initShare(); // opens the destination picker when this load is an Android share hand-off
initMcpOAuthReturn(); // surfaces the result when this load is an OAuth callback return
input.focus();
