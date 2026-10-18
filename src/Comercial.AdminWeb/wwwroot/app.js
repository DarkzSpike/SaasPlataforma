'use strict';
// Painel do desenvolvedor. Regras: token só em memória; nada de innerHTML com dados;
// toda operação é validada e auditada no servidor — esta interface apenas a solicita.

const state = { api: '', token: null, me: null, view: 'tenants', tenantId: null, tenant: null, tab: 'resumo' };

function h(tag, attrs = {}, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = v;
    else if (k === 'value') el.value = v;
    else if (k === 'style') el.style.cssText = v; // CSSOM: permitido pela CSP (atributo style inline não é)
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat(Infinity)) {
    if (kid === null || kid === undefined || kid === false) continue;
    el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  }
  return el;
}

// Associa cada <label> ao campo seguinte (acessibilidade e leitores de tela).
let labelSeq = 0;
function linkLabels(root) {
  root.querySelectorAll('label:not([for])').forEach(l => {
    const next = l.nextElementSibling;
    if (next && /^(INPUT|SELECT|TEXTAREA)$/.test(next.tagName)) { next.id ||= 'f' + (++labelSeq); l.htmlFor = next.id; }
  });
}

const money = c => (c / 100).toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
const dt = s => s ? new Date(s).toLocaleString('pt-BR') : '—';
const licensePill = s => h('span', { class: 'pill ' + ({ active: 'ok', expiring: 'warn', grace: 'warn' }[s] || 'bad') }, s);

async function call(method, path, body, headers = {}) {
  const opts = { method, headers: { ...headers } };
  if (state.token) opts.headers.Authorization = 'Bearer ' + state.token;
  if (body instanceof FormData) opts.body = body;
  else if (body !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
  const res = await fetch(state.api + path, opts);
  if (res.status === 401 && path !== '/admin/v1/auth/login' && !headers['X-Reauth-Totp']) { logout('Sessão expirada. Entre novamente.'); throw new Error('unauthorized'); }
  const text = await res.text();
  const data = text ? JSON.parse(text) : null;
  if (!res.ok) { const e = new Error((data && data.title) || ('HTTP ' + res.status)); e.code = data && data.code; throw e; }
  return data;
}

async function blobUrl(path) {
  const res = await fetch(state.api + path, { headers: { Authorization: 'Bearer ' + state.token } });
  return res.ok ? URL.createObjectURL(await res.blob()) : null;
}

function logout(msg) { state.token = null; state.me = null; render(msg); }

// ---------- Confirmação de operações críticas ----------
function confirmDialog({ title, text, tenantCode, totp, reason }) {
  return new Promise(resolve => {
    const err = h('div', { class: 'error' });
    const codeIn = h('input', { placeholder: 'Digite o código da empresa: ' + (tenantCode || ''), autocomplete: 'off' });
    const totpIn = h('input', { placeholder: 'Código do autenticador (6 dígitos)', inputmode: 'numeric', maxlength: '6', autocomplete: 'one-time-code' });
    const reasonIn = h('textarea', { rows: '2', placeholder: 'Motivo (registrado na auditoria)' });
    const dlg = h('dialog', {},
      h('h3', {}, title), h('p', {}, text),
      tenantCode ? [h('label', {}, 'Confirmação da empresa afetada'), codeIn] : null,
      reason ? [h('label', {}, 'Motivo'), reasonIn] : null,
      totp ? [h('label', {}, 'Reautenticação (MFA)'), totpIn] : null,
      err,
      h('div', { class: 'row', style: 'margin-top:12px;justify-content:flex-end' },
        h('button', { class: 'btn secondary', onclick: () => { dlg.close(); dlg.remove(); resolve(null); } }, 'Cancelar'),
        h('button', { class: 'btn danger', onclick: () => {
          if (tenantCode && codeIn.value !== tenantCode) { err.textContent = 'O código digitado não corresponde à empresa selecionada.'; return; }
          if (reason && reasonIn.value.trim().length < 5) { err.textContent = 'Informe o motivo.'; return; }
          dlg.close(); dlg.remove(); resolve({ code: codeIn.value, totp: totpIn.value, reason: reasonIn.value.trim() });
        } }, 'Confirmar')));
    document.body.append(dlg);
    linkLabels(dlg);
    dlg.showModal();
  });
}

// ---------- Telas ----------
function loginView(msg) {
  const email = h('input', { type: 'email', autocomplete: 'username' });
  const pwd = h('input', { type: 'password', autocomplete: 'current-password' });
  const otp = h('input', { inputmode: 'numeric', maxlength: '6', autocomplete: 'one-time-code' });
  const err = h('div', { class: 'error' }, msg || '');
  const submit = async () => {
    err.textContent = '';
    try {
      const r = await call('POST', '/admin/v1/auth/login', { email: email.value, password: pwd.value, totpCode: otp.value });
      state.token = r.accessToken;
      state.me = await call('GET', '/admin/v1/me');
      render();
    } catch (e) { err.textContent = e.message; }
  };
  otp.addEventListener('keydown', e => { if (e.key === 'Enter') submit(); });
  return h('div', { class: 'card login' },
    h('h2', {}, 'Painel do Desenvolvedor'),
    h('p', { class: 'muted' }, 'Acesso restrito à equipe autorizada do SaaS. MFA obrigatório.'),
    h('label', {}, 'E-mail'), email, h('label', {}, 'Senha'), pwd, h('label', {}, 'Código do autenticador'), otp,
    h('div', { style: 'margin-top:14px' }, h('button', { class: 'btn', onclick: submit }, 'Entrar')), err);
}

async function tenantsView() {
  const q = h('input', { placeholder: 'Buscar por nome, código ou documento' });
  const tbody = h('tbody');
  const load = async () => {
    const list = await call('GET', '/admin/v1/tenants?q=' + encodeURIComponent(q.value));
    tbody.replaceChildren(...list.map(t => h('tr', { class: 'clickable', onclick: () => openTenant(t.id) },
      h('td', {}, h('strong', {}, t.tradeName), h('div', { class: 'muted' }, t.code)),
      h('td', {}, t.plan || '—'), h('td', {}, licensePill(t.license)), h('td', {}, t.status), h('td', {}, dt(t.periodEnd)))));
  };
  q.addEventListener('input', () => { clearTimeout(q._t); q._t = setTimeout(load, 250); });
  await load();
  return [
    h('div', { class: 'card' }, h('div', { class: 'row' }, q, h('button', { class: 'btn', onclick: () => { state.view = 'new'; render(); } }, 'Nova empresa'))),
    h('div', { class: 'card' }, h('table', {}, h('thead', {}, h('tr', {}, ['Empresa', 'Plano', 'Licença', 'Situação', 'Fim do período'].map(x => h('th', {}, x)))), tbody)),
  ];
}

async function newTenantView() {
  const plans = await call('GET', '/admin/v1/plans');
  const f = {};
  const field = (k, label, attrs = {}) => { f[k] = h('input', attrs); return [h('label', {}, label), f[k]]; };
  const plan = h('select', {}, plans.map(p => h('option', { value: p.code }, `${p.name} — ${money(p.priceCents)}`)));
  const err = h('div', { class: 'error' });
  return h('div', { class: 'card' }, h('h2', {}, 'Nova empresa'),
    h('div', { class: 'grid' },
      h('div', {}, field('code', 'Código interno (minúsculas, números, hífen)'), field('legalName', 'Razão social'), field('tradeName', 'Nome fantasia'), field('document', 'CNPJ/CPF'),
        h('label', {}, 'Plano'), plan, field('storeName', 'Nome da loja principal', { value: 'Matriz' })),
      h('div', {}, field('ownerName', 'Nome do proprietário'), field('ownerEmail', 'E-mail do proprietário', { type: 'email' }),
        field('ownerPassword', 'Senha inicial (mín. 10, letras e números)', { type: 'password', autocomplete: 'new-password' }))),
    h('div', { class: 'row', style: 'margin-top:12px' },
      h('button', { class: 'btn', onclick: async () => {
        try {
          const r = await call('POST', '/admin/v1/tenants', { code: f.code.value, legalName: f.legalName.value, tradeName: f.tradeName.value, document: f.document.value || null,
            planCode: plan.value, storeName: f.storeName.value, owner: { email: f.ownerEmail.value, displayName: f.ownerName.value, password: f.ownerPassword.value } });
          openTenant(r.id);
        } catch (e) { err.textContent = e.message; }
      } }, 'Criar'),
      h('button', { class: 'btn secondary', onclick: () => { state.view = 'tenants'; render(); } }, 'Cancelar')), err);
}

async function openTenant(id) { state.tenantId = id; state.view = 'tenant'; state.tab = 'resumo'; render(); }

async function tenantView() {
  const d = await call('GET', `/admin/v1/tenants/${state.tenantId}`);
  state.tenant = d.tenant;
  const tabs = { resumo: 'Resumo', modulos: 'Módulos', suporte: 'Suporte', identidade: 'Identidade visual', auditoria: 'Auditoria' };
  const content = h('div');
  const tabBar = h('div', { class: 'row', style: 'margin-bottom:12px' }, Object.entries(tabs).map(([t, label]) =>
    h('button', { class: 'btn ' + (state.tab === t ? '' : 'secondary'), onclick: () => { state.tab = t; render(); } }, label)));
  const views = { resumo: tenantSummary, modulos: tenantModules, suporte: tenantSupport, identidade: brandingEditor, auditoria: tenantAudit };
  content.append(...[].concat(await views[state.tab](d)));
  return [tabBar, content];
}

function tenantSummary(d) {
  const t = d.tenant, s = d.subscription;
  const critical = async (path, title, needsReason) => {
    const c = await confirmDialog({ title, text: `Esta operação afeta a empresa ${t.tradeName} (${t.code}).`, tenantCode: t.code, totp: true, reason: needsReason });
    if (!c) return;
    try { await call('POST', path, { confirmTenantCode: c.code, reason: c.reason }, { 'X-Reauth-Totp': c.totp }); render(); } catch (e) { alert(e.message); }
  };
  return [
    h('div', { class: 'grid' },
      h('div', { class: 'card' }, h('h2', {}, 'Assinatura'),
        h('p', {}, 'Plano: ', h('strong', {}, s ? s.planCode : '—'), ' • ', s ? money(s.priceCents) : ''),
        h('p', {}, 'Licença: ', licensePill(d.license), ' • fim do período: ', dt(s && s.currentPeriodEnd)),
        h('p', { class: 'muted' }, `Limites: ${s ? s.maxUsers : 0} usuários, ${s ? s.maxDevices : 0} terminais, ${s ? s.maxStores : 0} lojas • ${d.activeUsers} usuários ativos`),
        h('div', { class: 'row' },
          h('button', { class: 'btn secondary', onclick: async () => { await call('POST', `/admin/v1/tenants/${t.id}/invoices`, { dueInDays: 5 }); render(); } }, 'Gerar fatura'),
          t.status === 1
            ? h('button', { class: 'btn danger', onclick: () => critical(`/admin/v1/tenants/${t.id}/suspend`, 'Suspender empresa', true) }, 'Suspender')
            : h('button', { class: 'btn', onclick: () => critical(`/admin/v1/tenants/${t.id}/reactivate`, 'Reativar empresa', false) }, 'Reativar'))),
      h('div', { class: 'card' }, h('h2', {}, 'Lojas'), h('ul', {}, d.stores.map(s => h('li', {}, `${s.name} (${s.code})`))),
        h('h2', {}, 'Faturas'), d.invoices.length ? h('ul', {}, d.invoices.map(i => h('li', {}, `${money(i.amountCents)} • vence ${dt(i.dueAt)} • ${['', 'aberta', 'paga', 'cancelada'][i.status]}`))) : h('p', { class: 'muted' }, 'Nenhuma fatura.'))),
    h('div', { class: 'card' }, h('h2', {}, 'Dispositivos'),
      h('table', {}, h('thead', {}, h('tr', {}, ['Nome', 'Plataforma', 'Situação', 'Versão', 'Última sincronização', 'Último erro'].map(x => h('th', {}, x)))),
        h('tbody', {}, d.devices.map(x => h('tr', {}, h('td', {}, x.name), h('td', {}, x.platform), h('td', {}, x.status), h('td', {}, x.appVersion || '—'), h('td', {}, dt(x.lastSyncAt)), h('td', {}, x.lastError || '—')))))),
  ];
}

async function tenantModules(d) {
  const catalog = await call('GET', '/admin/v1/modules');
  const tm = Object.fromEntries(d.modules.map(m => [m.moduleKey, m]));
  const effective = new Set(d.effectiveModules);
  const t = d.tenant;
  const statusName = ['Planejado', 'Beta', 'Disponível', 'Descontinuado'];
  const toggle = async (m, enable) => {
    const impact = await call('GET', `/admin/v1/tenants/${t.id}/modules/${m.key}/impact?enable=${enable}`);
    if (!impact.allowed) { alert('Alteração bloqueada: ' + impact.blockReason); return; }
    const c = await confirmDialog({ title: `${enable ? 'Ativar' : 'Desativar'} ${m.name}`, text: impact.explanation, tenantCode: t.code, totp: true, reason: true });
    if (!c) return;
    try { await call('POST', `/admin/v1/tenants/${t.id}/modules/${m.key}/${enable ? 'enable' : 'disable'}`, { confirmTenantCode: c.code, reason: c.reason }, { 'X-Reauth-Totp': c.totp }); render(); }
    catch (e) { alert(e.message); }
  };
  const groups = {};
  catalog.filter(m => !m.isCore).forEach(m => (groups[m.category] ||= []).push(m));
  return Object.entries(groups).map(([g, mods]) => h('div', { class: 'card' }, h('h2', {}, g),
    h('table', {}, h('tbody', {}, mods.map(m => {
      const on = tm[m.key] && tm[m.key].enabled;
      return h('tr', {},
        h('td', {}, h('strong', {}, m.name), h('div', { class: 'muted' }, m.description),
          m.dependencies.length ? h('div', { class: 'muted' }, 'Depende de: ' + m.dependencies.map(x => x.dependsOnKey).join(', ')) : null),
        h('td', {}, h('span', { class: 'pill' }, statusName[m.status])),
        h('td', {}, on ? h('span', { class: 'pill ' + (effective.has(m.key) ? 'ok' : 'warn') }, effective.has(m.key) ? 'ativo' : 'habilitado, sem efeito') : h('span', { class: 'pill' }, 'desativado'),
          tm[m.key] && tm[m.key].validUntil ? h('div', { class: 'muted' }, 'até ' + dt(tm[m.key].validUntil)) : null),
        h('td', {}, m.status === 0 ? h('span', { class: 'muted' }, 'não implementado')
          : h('button', { class: 'btn ' + (on ? 'secondary' : ''), onclick: () => toggle(m, !on) }, on ? 'Desativar' : 'Ativar')));
    })))));
}

async function tenantSupport(d) {
  const t = d.tenant;
  const out = h('div');
  const reason = h('input', { placeholder: 'Motivo do atendimento (mín. 10 caracteres)' });
  const minutes = h('select', {}, [15, 30, 60, 120].map(m => h('option', { value: m }, m + ' min')));
  const start = async () => {
    try {
      const s = await call('POST', `/admin/v1/tenants/${t.id}/support-sessions`, { reason: reason.value, durationMinutes: +minutes.value });
      const diag = await call('GET', `/admin/v1/support-sessions/${s.id}/diagnostics`);
      out.replaceChildren(
        h('p', {}, 'Sessão ', h('strong', {}, s.id), ' — modo consulta até ', dt(s.expiresAt)),
        h('pre', {}, JSON.stringify(diag, null, 2)),
        h('div', { class: 'row' },
          h('button', { class: 'btn secondary', onclick: async () => {
            const c = await confirmDialog({ title: 'Solicitar elevação temporária', text: 'O proprietário da empresa precisa aprovar. Somente cadastro de produtos.', totp: true, reason: true });
            if (!c) return;
            try { await call('POST', `/admin/v1/support-sessions/${s.id}/elevation-requests`, { reason: c.reason, operations: ['products.write'], durationMinutes: 15, totpCode: c.totp }); alert('Solicitação enviada para aprovação do cliente.'); }
            catch (e) { alert(e.message); }
          } }, 'Solicitar elevação'),
          h('button', { class: 'btn danger', onclick: async () => { await call('POST', `/admin/v1/support-sessions/${s.id}/end`, {}); out.replaceChildren(h('p', {}, 'Sessão encerrada.')); } }, 'Encerrar sessão')));
    } catch (e) { out.replaceChildren(h('div', { class: 'error' }, e.message)); }
  };
  return h('div', { class: 'card' }, h('h2', {}, 'Suporte técnico'),
    h('p', { class: 'muted' }, 'Acesso de consulta por padrão, com prazo e auditoria. Vendas, caixa e emissão fiscal nunca são liberados ao suporte.'),
    h('div', { class: 'row' }, reason, minutes, h('button', { class: 'btn', onclick: start }, 'Iniciar sessão')), out);
}

async function tenantAudit(d) {
  const rows = await call('GET', `/admin/v1/tenants/${d.tenant.id}/audit?take=200`);
  return h('div', { class: 'card' }, h('h2', {}, 'Auditoria da empresa'),
    h('table', {}, h('thead', {}, h('tr', {}, ['Quando', 'Ação', 'Ator', 'Entidade', 'Resultado'].map(x => h('th', {}, x)))),
      h('tbody', {}, rows.map(a => h('tr', {}, h('td', {}, dt(a.occurredAt)), h('td', {}, a.action),
        h('td', {}, ['Sistema', 'Usuário', 'Admin', 'Suporte', 'Dispositivo', 'Webhook'][a.actorType] + (a.supportSessionId ? ' (sessão de suporte)' : '')),
        h('td', {}, (a.entityType || '') + ' ' + (a.entityId || '')), h('td', {}, a.outcome || ''))))));
}

// ---------- Identidade visual (White Label) ----------
async function brandingEditor(d) {
  const t = d.tenant;
  const base = `/admin/v1/tenants/${t.id}/branding`;
  let cfg = await call('GET', base + '/draft');
  const presets = await call('GET', base + '/presets');
  const versions = await call('GET', base + '/versions');
  const published = await call('GET', base + '/published');
  const msg = h('div', { class: 'muted' });
  const preview = h('div', { class: 'preview-wrap' });
  const logoUrls = {};

  const colorFields = [['primaryColor', 'Primária'], ['secondaryColor', 'Secundária'], ['accentColor', 'Destaque'], ['backgroundColor', 'Fundo'], ['surfaceColor', 'Superfície'],
    ['textColor', 'Texto'], ['borderColor', 'Bordas'], ['darkBackgroundColor', 'Fundo (escuro)'], ['darkSurfaceColor', 'Superfície (escuro)'], ['darkTextColor', 'Texto (escuro)']];
  const textFields = [['systemName', 'Nome do sistema'], ['companyName', 'Nome da empresa'], ['welcomeMessage', 'Mensagem de boas-vindas'], ['reportHeader', 'Cabeçalho de relatórios']];
  const selects = [['theme', 'Tema', ['moderno', 'minimalista', 'elegante', 'vibrante', 'classico', 'alto_contraste']], ['fontFamily', 'Fonte', ['Inter', 'Roboto', 'Open Sans', 'Lato', 'Source Sans 3', 'Segoe UI']],
    ['buttonStyle', 'Botões', ['rounded', 'square', 'pill']], ['cardStyle', 'Cartões', ['flat', 'elevated', 'outlined']], ['density', 'Densidade', ['compact', 'normal', 'comfortable']], ['defaultMode', 'Modo padrão', ['light', 'dark', 'system']]];
  const inputs = {};

  const radius = () => ({ square: '0', pill: '999px', rounded: '6px' }[cfg.buttonStyle] || '6px');
  const renderPreview = () => {
    const logo = cfg.logoAssetId && logoUrls[cfg.logoAssetId] ? h('img', { src: logoUrls[cfg.logoAssetId], alt: '' }) : null;
    const screen = (title, dark, bodyFn) => {
      const bg = dark ? cfg.darkBackgroundColor : cfg.backgroundColor, sf = dark ? cfg.darkSurfaceColor : cfg.surfaceColor, tx = dark ? cfg.darkTextColor : cfg.textColor;
      const box = h('div', { class: 'pv' });
      box.style.background = bg; box.style.color = tx; box.style.fontFamily = cfg.fontFamily + ', system-ui'; box.style.fontSize = (14 * cfg.fontScale) + 'px';
      const bar = h('div', { class: 'bar' }, logo ? logo.cloneNode() : null, `${cfg.systemName} — ${title}${dark ? ' (escuro)' : ''}`);
      bar.style.background = cfg.secondaryColor; bar.style.color = '#fff';
      const body = h('div', { class: 'body' }, bodyFn(sf, tx));
      box.append(bar, body);
      return box;
    };
    const tile = (sf, text) => { const e = h('div', { class: 'tile' }, text); e.style.background = sf; e.style.border = cfg.cardStyle === 'outlined' ? `1px solid ${cfg.borderColor}` : '0'; e.style.boxShadow = cfg.cardStyle === 'elevated' ? '0 1px 4px rgba(0,0,0,.15)' : 'none'; return e; };
    const btn = (text, color) => { const e = h('span', { class: 'pbtn' }, text); e.style.background = color || cfg.primaryColor; e.style.borderRadius = radius(); return e; };
    preview.replaceChildren(
      screen('Login', false, sf => [tile(sf, cfg.welcomeMessage), btn('Entrar')]),
      screen('Dashboard', false, sf => [tile(sf, 'Vendas hoje: R$ 1.234,00'), tile(sf, 'Estoque baixo: 3 itens')]),
      screen('PDV', false, sf => [tile(sf, 'Total: R$ 59,90'), btn('Finalizar', cfg.accentColor)]),
      screen('Relatórios', false, sf => [tile(sf, cfg.reportHeader || cfg.companyName), tile(sf, 'Faturamento do mês')]),
      screen('Dashboard', true, sf => [tile(sf, 'Vendas hoje: R$ 1.234,00'), btn('Detalhes')]),
      screen('App móvel', false, sf => [tile(sf, 'Caixa aberto • última sync 2 min'), btn('Ver vendas')]));
  };

  const bind = (k, el, conv = v => v) => { inputs[k] = el; el.value = cfg[k] ?? ''; el.addEventListener('input', () => { cfg[k] = conv(el.value); renderPreview(); }); return el; };
  const upload = async (kind, input) => {
    const fd = new FormData(); fd.append('kind', kind); fd.append('file', input.files[0]);
    try {
      const a = await call('POST', base + '/assets', fd);
      const key = { logo: 'logoAssetId', logo_dark: 'logoDarkAssetId', icon: 'iconAssetId', splash: 'splashAssetId' }[kind];
      cfg[key] = a.id; logoUrls[a.id] = await blobUrl(`${base}/assets/${a.id}`);
      msg.textContent = `Imagem enviada (${a.width}×${a.height}). Salve o rascunho para usá-la.`; renderPreview();
    } catch (e) { msg.textContent = 'Upload recusado: ' + e.message; }
  };
  if (cfg.logoAssetId) logoUrls[cfg.logoAssetId] = await blobUrl(`${base}/assets/${cfg.logoAssetId}`);

  const save = async () => { try { await call('PUT', base + '/draft', cfg); msg.textContent = 'Rascunho salvo.'; } catch (e) { msg.textContent = e.message; } };
  const publish = async () => {
    await save();
    const c = await confirmDialog({ title: 'Publicar identidade visual', text: `A nova identidade será aplicada em todos os terminais e aplicativos de ${t.tradeName}.`, tenantCode: t.code });
    if (!c) return;
    try { const v = await call('POST', base + '/publish', { confirmTenantCode: c.code, note: 'publicado pelo painel' }); msg.textContent = 'Publicada versão ' + v.version; render(); } catch (e) { msg.textContent = e.message; }
  };

  const form = h('div', {},
    h('label', {}, 'Tema predefinido'),
    h('select', { onchange: e => { const p = presets.find(x => x.theme === e.target.value); if (!p) return; cfg = { ...p, systemName: cfg.systemName, companyName: cfg.companyName, logoAssetId: cfg.logoAssetId, logoDarkAssetId: cfg.logoDarkAssetId, iconAssetId: cfg.iconAssetId, splashAssetId: cfg.splashAssetId }; Object.entries(inputs).forEach(([k, el]) => el.value = cfg[k] ?? ''); renderPreview(); } },
      h('option', { value: '' }, 'Aplicar tema...'), presets.map(p => h('option', { value: p.theme }, p.theme))),
    textFields.map(([k, l]) => [h('label', {}, l), bind(k, h('input', { maxlength: k === 'systemName' ? '60' : '200' }))]),
    h('div', { class: 'grid', style: 'grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:8px' },
      colorFields.map(([k, l]) => h('div', {}, h('label', {}, l), bind(k, h('input', { type: 'color' }))))),
    h('div', { class: 'grid', style: 'grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:8px' },
      selects.map(([k, l, opts]) => h('div', {}, h('label', {}, l), bind(k, h('select', {}, opts.map(o => h('option', { value: o }, o))))))),
    h('label', {}, 'Escala do texto (0,85 a 1,3)'), bind('fontScale', h('input', { type: 'number', min: '0.85', max: '1.3', step: '0.05' }), Number),
    ['logo', 'logo_dark', 'icon', 'splash'].map(kind => [h('label', {}, 'Imagem: ' + kind + ' (PNG/JPEG até 2 MB)'),
      h('input', { type: 'file', accept: 'image/png,image/jpeg', onchange: e => upload(kind, e.target) })]),
    h('div', { class: 'row', style: 'margin-top:12px' },
      h('button', { class: 'btn secondary', onclick: save }, 'Salvar rascunho'),
      h('button', { class: 'btn secondary', onclick: async () => { try { await call('POST', base + '/preview', cfg); msg.textContent = 'Configuração válida (nada foi publicado).'; } catch (e) { msg.textContent = e.message; } } }, 'Validar prévia'),
      h('button', { class: 'btn', onclick: publish }, 'Publicar'),
      h('button', { class: 'btn danger', onclick: async () => { const c = await confirmDialog({ title: 'Voltar à identidade padrão', text: 'O histórico de versões é preservado.', tenantCode: t.code }); if (c) { await call('POST', base + '/reset', { confirmTenantCode: c.code }); render(); } } }, 'Padrão do produto')),
    msg);

  renderPreview();
  return [
    h('div', { class: 'card' }, h('h2', {}, 'Identidade Visual e Personalização'),
      h('p', { class: 'muted' }, `Publicada: ${published.custom ? 'versão ' + published.version : 'padrão do produto'}. `,
        d.effectiveModules.includes('white_label') ? '' : 'Atenção: módulo White Label não ativo — a empresa verá a identidade padrão.'),
      h('div', { class: 'grid', style: 'grid-template-columns:minmax(260px,360px) 1fr' }, form, preview)),
    h('div', { class: 'card' }, h('h2', {}, 'Versões publicadas'),
      versions.length ? h('table', {}, h('tbody', {}, versions.map(v => h('tr', {}, h('td', {}, 'v' + v.version), h('td', {}, dt(v.publishedAt)), h('td', {}, v.note + (v.restoredFromVersion ? ` (restaurada de v${v.restoredFromVersion})` : '')),
        h('td', {}, h('button', { class: 'btn secondary', onclick: async () => { const c = await confirmDialog({ title: `Restaurar versão ${v.version}`, text: 'Cria uma nova versão com esta configuração.', tenantCode: t.code }); if (c) { await call('POST', `${base}/versions/${v.version}/restore`, { confirmTenantCode: c.code, note: 'restauração' }); render(); } } }, 'Restaurar'))))))
        : h('p', { class: 'muted' }, 'Nenhuma versão publicada.')),
  ];
}

async function plansView() {
  const plans = await call('GET', '/admin/v1/plans');
  return h('div', { class: 'card' }, h('h2', {}, 'Planos'),
    h('p', { class: 'muted' }, 'Alterações em planos não modificam assinaturas existentes (cada contrato guarda o preço e os limites da contratação).'),
    h('table', {}, h('thead', {}, h('tr', {}, ['Plano', 'Preço', 'Limites', 'Módulos'].map(x => h('th', {}, x)))),
      h('tbody', {}, plans.map(p => h('tr', {}, h('td', {}, h('strong', {}, p.name), h('div', { class: 'muted' }, p.code)), h('td', {}, money(p.priceCents) + (p.period === 12 ? '/ano' : '/mês')),
        h('td', {}, `${p.maxUsers} usuários • ${p.maxDevices} terminais • ${p.maxStores} lojas • ${p.graceDays} dias de tolerância`),
        h('td', {}, p.modules.map(m => m.moduleKey).join(', ')))))));
}

async function diagnosticsView() {
  const d = await call('GET', '/admin/v1/diagnostics');
  const chain = state.me.role === 'saas_admin' ? await call('GET', '/admin/v1/audit/verify') : null;
  return h('div', { class: 'grid' },
    h('div', { class: 'card' }, h('h2', {}, 'Saúde'), h('p', {}, 'Banco: ', h('span', { class: 'pill ' + (d.database === 'ok' ? 'ok' : 'bad') }, d.database)),
      h('p', {}, `${d.tenants} empresas • ${d.activeDevices} terminais ativos • ${d.devicesWithoutSync24h} sem sincronizar há 24h`),
      h('p', {}, `${d.syncRejected24h} operações recusadas na sincronização (24h) • ${d.fiscalPending} documentos fiscais pendentes`),
      h('p', { class: 'muted' }, d.backups)),
    h('div', { class: 'card' }, h('h2', {}, 'Eventos de segurança (24h)'), d.securityEvents24h.length ? h('ul', {}, d.securityEvents24h.map(e => h('li', {}, `${e.action}: ${e.count}`))) : h('p', { class: 'muted' }, 'Nenhum.')),
    h('div', { class: 'card' }, h('h2', {}, 'Versões instaladas'), h('ul', {}, d.versions.map(v => h('li', {}, `${v.version || 'desconhecida'}: ${v.count}`)))),
    chain ? h('div', { class: 'card' }, h('h2', {}, 'Integridade da auditoria'), h('p', {}, h('span', { class: 'pill ' + (chain.ok ? 'ok' : 'bad') }, chain.ok ? 'cadeia íntegra' : 'ADULTERAÇÃO DETECTADA no registro ' + chain.brokenAtId), ` • ${chain.records} registros`)) : null);
}

async function render(msg) {
  const root = document.getElementById('app');
  if (!state.token) { root.replaceChildren(loginView(msg)); linkLabels(root); return; }
  const nav = [['tenants', 'Empresas'], ['plans', 'Planos'], ['diagnostics', 'Diagnóstico']];
  const header = h('header', {}, h('h1', {}, 'Painel do Desenvolvedor'),
    h('nav', {}, nav.map(([k, l]) => h('button', { class: state.view === k ? 'active' : '', onclick: () => { state.view = k; state.tenantId = null; render(); } }, l))),
    h('div', { class: 'spacer' }), h('span', { class: 'muted' }, `${state.me.email} (${state.me.role})`),
    h('button', { class: 'btn secondary', onclick: () => logout() }, 'Sair'));
  const main = h('main', {}, h('p', { class: 'muted' }, 'Carregando...'));
  const parts = [header];
  if (state.view === 'tenant' && state.tenant && state.tenant.id === state.tenantId)
    parts.push(h('div', { class: 'tenant-banner' }, 'Empresa selecionada:', h('strong', {}, state.tenant.tradeName), h('span', {}, `(${state.tenant.code})`)));
  parts.push(main);
  root.replaceChildren(...parts);
  try {
    const views = { tenants: tenantsView, new: newTenantView, tenant: tenantView, plans: plansView, diagnostics: diagnosticsView };
    const content = await views[state.view]();
    main.replaceChildren(...[].concat(content));
    linkLabels(main);
    // O banner da empresa é atualizado após carregar o detalhe (sempre visível durante a manutenção).
    if (state.view === 'tenant' && !root.querySelector('.tenant-banner') && state.tenant)
      root.insertBefore(h('div', { class: 'tenant-banner' }, 'Empresa selecionada:', h('strong', {}, state.tenant.tradeName), h('span', {}, `(${state.tenant.code})`)), main);
  } catch (e) { if (e.message !== 'unauthorized') main.replaceChildren(h('div', { class: 'card error' }, e.message)); }
}

fetch('/config.json').then(r => r.json()).then(c => { state.api = c.apiBaseUrl.replace(/\/$/, ''); render(); });
