/* Agent 42 — element handles — every getElementById lookup, in one place. Split out of app.js (ADR 0041). */

import { wireTabs } from "./tabs.js";

export const conversationList = document.getElementById("conversationList");
export const searchInput = document.getElementById("searchInput");
export const newChatBtn = document.getElementById("newChatBtn");
export const thread = document.getElementById("thread");
export const chatScroll = document.getElementById("chatScroll");
export const emptyState = document.getElementById("emptyState");
export const form = document.getElementById("composerForm");
export const input = document.getElementById("promptInput");
export const attachBtn = document.getElementById("attachBtn");
export const micBtn = document.getElementById("micBtn");
export const voiceBtn = document.getElementById("voiceBtn");
export const stopBtn = document.getElementById("stopBtn");
export const fileInput = document.getElementById("fileInput");
export const attachmentPreview = document.getElementById("attachmentPreview");
export const modelSelect = document.getElementById("modelSelect");
export const modelField = document.getElementById("modelField");
export const noModelHint = document.getElementById("noModelHint");
export const agentLabel = document.getElementById("agentLabel");
export const menuToggle = document.getElementById("menuToggle");
export const collapseSidebarBtn = document.getElementById("collapseSidebarBtn");
export const backdrop = document.getElementById("backdrop");
export const app = document.querySelector(".app");

// Token-usage dashboard (ADR 0031) — a dedicated full-page overlay, opened from the sidebar footer.
export const openUsageBtn = document.getElementById("openUsageBtn");
export const usageView = document.getElementById("usageView");
export const usageCloseBtn = document.getElementById("usageCloseBtn");
export const usageApplication = document.getElementById("usageApplication");
export const usageUser = document.getElementById("usageUser");
export const usageRange = document.getElementById("usageRange");
export const usageRefresh = document.getElementById("usageRefresh");
export const usageTiles = {
  toolCalls: document.getElementById("usageTileToolCalls"),
  input: document.getElementById("usageTileInput"),
  output: document.getElementById("usageTileOutput"),
  cached: document.getElementById("usageTileCached"),
  calls: document.getElementById("usageTileCalls"),
  errors: document.getElementById("usageTileErrors"),
};
export const usageModelsBody = document.getElementById("usageModelsBody");
export const usageModelsEmpty = document.getElementById("usageModelsEmpty");
export const usageToolsBody = document.getElementById("usageToolsBody");
export const usageToolsEmpty = document.getElementById("usageToolsEmpty");
export const usageUsersBody = document.getElementById("usageUsersBody");
export const usageUsersEmpty = document.getElementById("usageUsersEmpty");
export const agentList = document.getElementById("agentList");
export const addAgentBtn = document.getElementById("addAgentBtn");
export const agentDialog = document.getElementById("agentDialog");
export const agentForm = document.getElementById("agentForm");
export const agentFormTitle = document.getElementById("agentFormTitle");
export const agentNameInput = document.getElementById("agentName");
export const agentDescriptionInput = document.getElementById("agentDescription");
export const agentModelSelect = document.getElementById("agentModelSelect");
export const agentSystemPromptInput = document.getElementById("agentSystemPrompt");
export const agentContinuousInput = document.getElementById("agentContinuous");
export const agentContinuousNote = document.getElementById("agentContinuousNote");
export const agentKindField = document.getElementById("agentKindField");
export const agentKindSelect = document.getElementById("agentKindSelect");
export const agentMcpExposedInput = document.getElementById("agentMcpExposed");
export const agentMcpUrl = document.getElementById("agentMcpUrl");
export const agentMcpUrlCopyBtn = document.getElementById("agentMcpUrlCopyBtn");
export const agentKeySection = document.getElementById("agentKeySection");
export const agentRegenerateKeyBtn = document.getElementById("agentRegenerateKeyBtn");
export const agentKeyReveal = document.getElementById("agentKeyReveal");
export const agentKeyValue = document.getElementById("agentKeyValue");
export const agentKeyCopyBtn = document.getElementById("agentKeyCopyBtn");
export const agentEmailSection = document.getElementById("agentEmailSection");
export const agentEmailValue = document.getElementById("agentEmailValue");
export const agentEmailCopyBtn = document.getElementById("agentEmailCopyBtn");
export const agentRegenerateEmailBtn = document.getElementById("agentRegenerateEmailBtn");
export const agentEmailNote = document.getElementById("agentEmailNote");
export const agentRunUrl = document.getElementById("agentRunUrl");
export const agentRunUrlCopyBtn = document.getElementById("agentRunUrlCopyBtn");
export const agentCancelBtn = document.getElementById("agentCancelBtn");
// The agent's outbound webhook panel (ADR 0094).
export const agentWebhookUnsaved = document.getElementById("agentWebhookUnsaved");
export const agentWebhookSection = document.getElementById("agentWebhookSection");
export const agentWebhookUrl = document.getElementById("agentWebhookUrl");
export const agentWebhookSaveBtn = document.getElementById("agentWebhookSaveBtn");
export const agentWebhookTestBtn = document.getElementById("agentWebhookTestBtn");
export const agentWebhookRotateBtn = document.getElementById("agentWebhookRotateBtn");
export const agentWebhookRemoveBtn = document.getElementById("agentWebhookRemoveBtn");
export const agentWebhookStatus = document.getElementById("agentWebhookStatus");
export const agentWebhookSecretReveal = document.getElementById("agentWebhookSecretReveal");
export const agentWebhookSecretValue = document.getElementById("agentWebhookSecretValue");
export const agentWebhookSecretCopyBtn = document.getElementById("agentWebhookSecretCopyBtn");
export const agentWebhookLog = document.getElementById("agentWebhookLog");

export const openProfileBtn = document.getElementById("openProfileBtn");
export const profileDialog = document.getElementById("profileDialog");
export const profileForm = document.getElementById("profileForm");
export const profileLanguage = document.getElementById("profileLanguage");
export const profileTheme = document.getElementById("profileTheme");
export const profileSuggest = document.getElementById("profileSuggest");
export const profileCancelBtn = document.getElementById("profileCancelBtn");
// The rail + drill-down shell. #profileNav is the element carrying role="tablist" — the rail click is
// delegated there rather than bound per tab, because the inline SVG icons mean e.target is usually a
// <path>. The back/detail-title pair and the × are the mobile chrome; the status line and the Tools
// badge are wired in profile.js.
export const profileNav = document.getElementById("profileNav");
export const prefsCloseBtn = document.getElementById("prefsCloseBtn");
export const prefsBackBtn = document.getElementById("prefsBackBtn");
export const prefsDetailTitle = document.getElementById("prefsDetailTitle");
export const prefsStatus = document.getElementById("prefsStatus");
export const toolsCount = document.getElementById("toolsCount");
export const locationEnableBtn = document.getElementById("locationEnableBtn");
export const locationStatus = document.getElementById("locationStatus");
export const pushSection = document.getElementById("pushSection");
export const pushStatus = document.getElementById("pushStatus");
export const pushToggleBtn = document.getElementById("pushToggleBtn");
// Preferences + agent dialog tabs. wireTabs (defined near openProfileDialog) takes [tab, panel]
// pairs in tablist order, attaches the click + keyboard nav, and returns a select(index, focus).
// Configuration and Skills are APPENDED, never inserted: initMcpOAuthReturn deep-links to
// select(3) (the MCP tab), so MCP must stay index 3 — a new tab placed before it would shift MCP and
// break the OAuth return. Append → MCP stays 3, Configuration is 4, Skills is 5.
export const profileTabs = wireTabs([
  [document.getElementById("tabGeneral"), document.getElementById("panelGeneral")],
  [document.getElementById("tabTools"), document.getElementById("panelTools")],
  [document.getElementById("tabProviders"), document.getElementById("panelProviders")],
  [document.getElementById("tabMcp"), document.getElementById("panelMcp")],
  [document.getElementById("tabConfig"), document.getElementById("panelConfig")],
  [document.getElementById("tabSkills"), document.getElementById("panelSkills")],
]);
export const agentTabs = wireTabs([
  [document.getElementById("tabAgentGeneral"), document.getElementById("panelAgentGeneral")],
  [document.getElementById("tabAgentTools"), document.getElementById("panelAgentTools")],
  [document.getElementById("tabAgentApi"), document.getElementById("panelAgentApi")],
  // APPENDED, never inserted: saveAgent selects the API-key tab as index 2.
  [document.getElementById("tabAgentWebhook"), document.getElementById("panelAgentWebhook")],
]);

export const defaultModelSelect = document.getElementById("defaultModelSelect");
export const embeddingModelSelect = document.getElementById("embeddingModelSelect");
export const speechModelSelect = document.getElementById("speechModelSelect");
export const providerList = document.getElementById("providerList");
export const addProviderBtn = document.getElementById("addProviderBtn");
export const providerDialog = document.getElementById("providerDialog");
export const providerForm = document.getElementById("providerForm");
export const providerFormTitle = document.getElementById("providerFormTitle");
export const providerKeyInput = document.getElementById("providerKey");
export const providerKindSelect = document.getElementById("providerKind");
export const providerDisplayNameInput = document.getElementById("providerDisplayName");
export const providerEndpointInput = document.getElementById("providerEndpoint");
export const providerApiKeyInput = document.getElementById("providerApiKey");
export const providerResponsesField = document.getElementById("providerResponsesField");
export const providerResponsesApi = document.getElementById("providerResponsesApi");
export const providerModelList = document.getElementById("providerModelList");
export const addModelRowBtn = document.getElementById("addModelRowBtn");
export const providerError = document.getElementById("providerError");
export const providerCancelBtn = document.getElementById("providerCancelBtn");

// Remote MCP servers (config-driven remote MCP servers) — mirrors the provider dialog.
export const mcpServerList = document.getElementById("mcpServerList");
export const addMcpServerBtn = document.getElementById("addMcpServerBtn");
// Uploaded agent skills (ADR 0049) — mirrors the MCP server list, minus an editor: a skill IS a file,
// so "edit" is "upload again" and the server replaces by name.
export const skillList = document.getElementById("skillList");
export const addSkillBtn = document.getElementById("addSkillBtn");
export const skillFileInput = document.getElementById("skillFile");
export const skillStatus = document.getElementById("skillStatus");
export const configHealthList = document.getElementById("configHealthList");
export const runConfigProbesBtn = document.getElementById("runConfigProbesBtn");
export const searxngUiLink = document.getElementById("searxngUiLink");
export const mcpDialog = document.getElementById("mcpDialog");
export const mcpForm = document.getElementById("mcpForm");
export const mcpFormTitle = document.getElementById("mcpFormTitle");
export const mcpNameInput = document.getElementById("mcpName");
export const mcpEndpointInput = document.getElementById("mcpEndpoint");
export const mcpEndpointField = document.getElementById("mcpEndpointField");
export const mcpBridgeSection = document.getElementById("mcpBridgeSection");
export const mcpBridgeRegenerateBtn = document.getElementById("mcpBridgeRegenerateBtn");
export const mcpBridgeKeyReveal = document.getElementById("mcpBridgeKeyReveal");
export const mcpBridgeKeyValue = document.getElementById("mcpBridgeKeyValue");
export const mcpBridgeKeyCopyBtn = document.getElementById("mcpBridgeKeyCopyBtn");
export const mcpBridgeCommand = document.getElementById("mcpBridgeCommand");
export const mcpEnabledInput = document.getElementById("mcpEnabled");
export const mcpSeedsUrlsInput = document.getElementById("mcpSeedsUrls");
export const mcpAuthSelect = document.getElementById("mcpAuth");
export const mcpHeaderNameField = document.getElementById("mcpHeaderNameField");
export const mcpHeaderNameInput = document.getElementById("mcpHeaderName");
export const mcpHeaderValueField = document.getElementById("mcpHeaderValueField");
export const mcpHeaderValueInput = document.getElementById("mcpHeaderValue");
export const mcpAuthorityField = document.getElementById("mcpAuthorityField");
export const mcpAuthorityInput = document.getElementById("mcpAuthority");
export const mcpClientIdField = document.getElementById("mcpClientIdField");
export const mcpClientIdInput = document.getElementById("mcpClientId");
export const mcpClientSecretField = document.getElementById("mcpClientSecretField");
export const mcpClientSecretInput = document.getElementById("mcpClientSecret");
export const mcpScopesField = document.getElementById("mcpScopesField");
export const mcpScopesInput = document.getElementById("mcpScopes");
export const mcpOAuthNote = document.getElementById("mcpOAuthNote");
export const mcpError = document.getElementById("mcpError");
export const mcpCancelBtn = document.getElementById("mcpCancelBtn");
export const mcpTestBtn = document.getElementById("mcpTestBtn");
export const mcpConnectBtn = document.getElementById("mcpConnectBtn");
export const mcpTestResult = document.getElementById("mcpTestResult");
export const mcpTabResult = document.getElementById("mcpTabResult");

// Selectable tools (ADR 0021): checkbox groups in the agent + preferences dialogs, and a per-chat popover.
export const agentToolChecks = document.getElementById("agentToolChecks");
export const profileToolChecks = document.getElementById("profileToolChecks");
export const toolsBtn = document.getElementById("toolsBtn");
export const incognitoBtn = document.getElementById("incognitoBtn");
export const toolsDialog = document.getElementById("toolsDialog");
export const chatToolChecks = document.getElementById("chatToolChecks");
export const toolsDoneBtn = document.getElementById("toolsDoneBtn");

// Web Share Target picker (Android PWA, ADR 0023): a shared payload lands on a /?share=1 load.
export const shareDialog = document.getElementById("shareDialog");
export const shareAgentList = document.getElementById("shareAgentList");
export const shareNewChatBtn = document.getElementById("shareNewChatBtn");
export const shareCancelBtn = document.getElementById("shareCancelBtn");

// Read-only share link (ADR 0035): mint/share-or-copy/revoke a public, no-login link to one conversation.
// Distinct from the ADR 0023 Web Share Target above (which shares INTO the app).
export const shareLinkDialog = document.getElementById("shareLinkDialog");
export const shareLinkConvTitle = document.getElementById("shareLinkConvTitle");
export const shareLinkCreateBtn = document.getElementById("shareLinkCreateBtn");
export const shareLinkResult = document.getElementById("shareLinkResult");
export const shareLinkUrl = document.getElementById("shareLinkUrl");
export const shareLinkShareBtn = document.getElementById("shareLinkShareBtn");
export const shareLinkCopyBtn = document.getElementById("shareLinkCopyBtn");
export const shareLinkRevokeBtn = document.getElementById("shareLinkRevokeBtn");
export const shareLinkCloseBtn = document.getElementById("shareLinkCloseBtn");
export const exportMarkdownLink = document.getElementById("exportMarkdownLink");
export const exportJsonLink = document.getElementById("exportJsonLink");
export const exportZipLink = document.getElementById("exportZipLink");
export const exportAllBtn = document.getElementById("exportAllBtn");
export const exportAllStatus = document.getElementById("exportAllStatus");
// Second entry point into the same dialog, in the persistent topbar cluster beside #incognitoBtn.
export const topbarShareBtn = document.getElementById("topbarShareBtn");
// The chat's artifacts list (ADR 0084): reaches the artifact pane without a card. Hidden until the chat has one.
export const artifactsBtn = document.getElementById("artifactsBtn");

// Custom conversation groups (ADR 0073): the transmissions-header "+", the shared create/rename name
// dialog, and the move-to-group picker that is the touch path for what a mouse does by dragging.
export const addGroupBtn = document.getElementById("addGroupBtn");
export const groupNameDialog = document.getElementById("groupNameDialog");
export const groupNameTitle = document.getElementById("groupNameTitle");
export const groupNameInput = document.getElementById("groupNameInput");
export const groupNameSaveBtn = document.getElementById("groupNameSaveBtn");
export const groupNameCancelBtn = document.getElementById("groupNameCancelBtn");
export const moveToGroupDialog = document.getElementById("moveToGroupDialog");
export const moveToGroupConvTitle = document.getElementById("moveToGroupConvTitle");
export const moveToGroupList = document.getElementById("moveToGroupList");
export const moveToGroupSaveBtn = document.getElementById("moveToGroupSaveBtn");
export const moveToGroupCancelBtn = document.getElementById("moveToGroupCancelBtn");
// Client tools (ADR 0089): the model asks the user to approve an action.
export const confirmActionDialog = document.getElementById("confirmActionDialog");
export const confirmActionText = document.getElementById("confirmActionText");
export const confirmActionApproveBtn = document.getElementById("confirmActionApproveBtn");
export const confirmActionDeclineBtn = document.getElementById("confirmActionDeclineBtn");
// Shared state (ADR 0089): the panel above the composer.
export const sharedStatePanel = document.getElementById("sharedStatePanel");
