'use strict';
// Painel do desenvolvedor. Regras: token só em memória; nada de innerHTML com dados;
// toda operação é validada e auditada no servidor — esta interface apenas a solicita.

const state = { api: '', token: null, me: null, view: 'tenants', tenantId: null, tenant: null, tab: 'resumo', support: null, invoiceDays: 3 };

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

// Chamada à API da loja dentro de uma sessão de suporte (o servidor confere prazo, escopo e registra tudo).
function callStore(method, path, body) {
  if (!state.support) throw new Error('Sessão de suporte não iniciada.');
  return call(method, path, body, { 'X-Support-Session': state.support.id });
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

/** Diálogo com campos (data, valor...) + confirmação da empresa, motivo e MFA. Devolve os valores ou null. */
function formDialog({ title, text, fields, tenantCode, totp, reason, confirmText }) {
  return new Promise(resolve => {
    const err = h('div', { class: 'error' });
    const inputs = fields.map(f => {
      const el = f.type === 'checkbox' ? h('input', { type: 'checkbox' }) : h('input', { type: f.type || 'text', value: f.value ?? '', placeholder: f.placeholder || '', min: f.min, step: f.step });
      if (f.type === 'checkbox') el.checked = !!f.value;
      return { f, el };
    });
    const codeIn = h('input', { placeholder: 'Digite o código da empresa: ' + (tenantCode || ''), autocomplete: 'off' });
    const totpIn = h('input', { placeholder: 'Código do autenticador (6 dígitos)', inputmode: 'numeric', maxlength: '6', autocomplete: 'one-time-code' });
    const reasonIn = h('textarea', { rows: '2', placeholder: 'Motivo (registrado na auditoria)' });
    const dlg = h('dialog', {},
      h('h3', {}, title), text ? h('p', {}, text) : null,
      inputs.map(({ f, el }) => f.type === 'checkbox' ? h('label', { class: 'row' }, el, ' ' + f.label) : [h('label', {}, f.label), el, f.help ? h('div', { class: 'muted' }, f.help) : null]),
      tenantCode ? [h('label', {}, 'Confirmação da empresa afetada'), codeIn] : null,
      reason ? [h('label', {}, 'Motivo'), reasonIn] : null,
      totp ? [h('label', {}, 'Reautenticação (MFA)'), totpIn] : null,
      err,
      h('div', { class: 'row', style: 'margin-top:12px;justify-content:flex-end' },
        h('button', { class: 'btn secondary', onclick: () => { dlg.close(); dlg.remove(); resolve(null); } }, 'Cancelar'),
        h('button', { class: 'btn', onclick: () => {
          if (tenantCode && codeIn.value !== tenantCode) { err.textContent = 'O código digitado não corresponde à empresa selecionada.'; return; }
          if (reason && reasonIn.value.trim().length < 5) { err.textContent = 'Informe o motivo.'; return; }
          const values = {};
          for (const { f, el } of inputs) values[f.key] = f.type === 'checkbox' ? el.checked : el.value.trim();
          dlg.close(); dlg.remove(); resolve({ values, code: codeIn.value, totp: totpIn.value, reason: reasonIn.value.trim() });
        } }, confirmText || 'Confirmar')));
    document.body.append(dlg);
    linkLabels(dlg);
    dlg.showModal();
  });
}

/** "19,90" ou "19.90" → 1990 (null se vazio, NaN se inválido). */
function toCents(v) {
  if (!v) return null;
  const n = Number(String(v).replace(/[R$\s]/g, '').replace(/\.(?=\d{3}(\D|$))/g, '').replace(',', '.'));
  return Number.isFinite(n) && n > 0 ? Math.round(n * 100) : NaN;
}
const isoDate = d => { const x = new Date(d); return new Date(x.getTime() - x.getTimezoneOffset() * 60000).toISOString().slice(0, 10); };
const addDays = n => isoDate(Date.now() + n * 86400000);

/** Ações de uma fatura em aberto (empresa e lista geral). */
function invoiceActions(inv, tenant) {
  if (!(inv.status === 1 || inv.status === 'aberta')) return null;
  const edit = async () => {
    const c = await formDialog({ title: 'Editar fatura', text: `${tenant.tradeName}: fatura de ${money(inv.amountCents)} com vencimento em ${dt(inv.dueAt)}. Se o cliente já gerou PIX ou link de pagamento, eles são cancelados e o cliente é avisado.`,
      fields: [{ key: 'due', label: 'Novo vencimento', type: 'date', value: isoDate(inv.dueAt) }, { key: 'amount', label: 'Novo valor (R$)', value: (inv.amountCents / 100).toFixed(2).replace('.', ',') }],
      tenantCode: tenant.code, totp: true, reason: true, confirmText: 'Salvar alterações' });
    if (!c) return;
    const cents = toCents(c.values.amount);
    if (Number.isNaN(cents)) { alert('Valor inválido.'); return; }
    try {
      await call('PUT', `/admin/v1/invoices/${inv.id}`, { dueDate: c.values.due || null, amountCents: cents === inv.amountCents ? null : cents, confirmTenantCode: c.code, reason: c.reason }, { 'X-Reauth-Totp': c.totp });
      render();
    } catch (e) { alert(e.message); }
  };
  const cancel = async () => {
    const c = await confirmDialog({ title: 'Cancelar fatura', text: `${tenant.tradeName}: ${money(inv.amountCents)} (vence ${dt(inv.dueAt)}). O PIX/link gerado deixa de valer.`, tenantCode: tenant.code, totp: true, reason: true });
    if (!c) return;
    try { await call('POST', `/admin/v1/invoices/${inv.id}/cancel`, { confirmTenantCode: c.code, reason: c.reason }, { 'X-Reauth-Totp': c.totp }); render(); } catch (e) { alert(e.message); }
  };
  const paid = async () => {
    const c = await confirmDialog({ title: 'Baixar fatura manualmente', text: `Use só se o pagamento entrou fora do Mercado Pago (ex.: transferência). ${tenant.tradeName} • ${money(inv.amountCents)}.`, tenantCode: tenant.code, totp: true, reason: true });
    if (!c) return;
    try { await call('POST', `/admin/v1/invoices/${inv.id}/mark-paid`, { confirmTenantCode: c.code, reason: c.reason }, { 'X-Reauth-Totp': c.totp }); render(); } catch (e) { alert(e.message); }
  };
  return h('div', { class: 'row' },
    h('button', { class: 'btn secondary', onclick: edit }, 'Editar'),
    h('button', { class: 'btn secondary', onclick: paid }, 'Marcar como paga'),
    h('button', { class: 'btn danger', onclick: cancel }, 'Cancelar'));
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
      h('td', {}, t.plan || '—'), h('td', {}, licensePill(t.license)), h('td', {}, t.status), h('td', {}, dt(t.periodEnd)),
      h('td', {}, h('button', { class: 'btn secondary', onclick: e => { e.stopPropagation(); openTenant(t.id, 'loja'); } }, 'Acessar loja')))));
  };
  q.addEventListener('input', () => { clearTimeout(q._t); q._t = setTimeout(load, 250); });
  await load();
  return [
    h('div', { class: 'card' }, h('div', { class: 'row' }, q, h('button', { class: 'btn', onclick: () => { state.view = 'new'; render(); } }, 'Nova empresa'))),
    h('div', { class: 'card' }, h('table', {}, h('thead', {}, h('tr', {}, ['Empresa', 'Plano', 'Licença', 'Situação', 'Fim do período', ''].map(x => h('th', {}, x)))), tbody)),
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

async function openTenant(id, tab) {
  if (state.support && state.support.tenantId !== id) state.support = null; // sessão de suporte é sempre de UMA empresa
  state.tenantId = id; state.view = 'tenant'; state.tab = tab || 'resumo'; render();
}

async function tenantView() {
  const d = await call('GET', `/admin/v1/tenants/${state.tenantId}`);
  state.tenant = d.tenant;
  const tabs = { resumo: 'Resumo', loja: 'Acessar loja (suporte)', modulos: 'Módulos', armazenamento: 'Armazenamento', suporte: 'Diagnóstico', identidade: 'Identidade visual', auditoria: 'Auditoria' };
  const content = h('div');
  const tabBar = h('div', { class: 'row', style: 'margin-bottom:12px' }, Object.entries(tabs).map(([t, label]) =>
    h('button', { class: 'btn ' + (state.tab === t ? '' : 'secondary'), onclick: () => { state.tab = t; render(); } }, label)));
  const views = { resumo: tenantSummary, loja: storeWorkspace, modulos: tenantModules, armazenamento: tenantStorage, suporte: tenantSupport, identidade: brandingEditor, auditoria: tenantAudit };
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
          h('button', { class: 'btn', onclick: () => { state.tab = 'loja'; render(); } }, 'Acessar loja (suporte)'),
          h('button', { class: 'btn secondary', onclick: async () => {
            const c = await formDialog({ title: 'Gerar fatura agora', text: `Cria a fatura do próximo período de ${t.tradeName} já agora (serve também para testar). Se já existir uma em aberto, nada é duplicado.`,
              fields: [{ key: 'due', label: 'Vencimento', type: 'date', value: addDays(5) },
                       { key: 'amount', label: 'Valor (R$)', value: s ? (s.priceCents / 100).toFixed(2).replace('.', ',') : '', help: 'Padrão: valor do plano.' },
                       { key: 'notify', label: 'Avisar o cliente (aviso no sistema + e-mail)', type: 'checkbox', value: true }], confirmText: 'Gerar fatura' });
            if (!c) return;
            const cents = toCents(c.values.amount);
            if (Number.isNaN(cents)) { alert('Valor inválido.'); return; }
            try {
              const r = await call('POST', `/admin/v1/tenants/${t.id}/invoices`, { dueDate: c.values.due || null, amountCents: cents, notify: c.values.notify });
              if (r.alreadyExisted) alert(`Já existe uma fatura em aberto deste período (${money(r.amountCents)}, vence ${dt(r.dueAt)}). Use "Editar" na lista de faturas para mudar a data ou o valor.`);
              render();
            } catch (e) { alert(e.message); }
          } }, 'Gerar fatura agora'),
          h('button', { class: 'btn secondary', onclick: async () => {
            const c = await formDialog({ title: 'Mudar dia de cobrança', text: `Próximo vencimento atual: ${dt(s && s.currentPeriodEnd)}. A partir da data escolhida, as cobranças seguem esse dia (todo mês). A fatura em aberto do próximo período acompanha.`,
              fields: [{ key: 'date', label: 'Novo próximo vencimento', type: 'date', value: s ? isoDate(s.currentPeriodEnd) : addDays(30) }], tenantCode: t.code, totp: true, reason: true, confirmText: 'Salvar' });
            if (!c) return;
            try { await call('PUT', `/admin/v1/tenants/${t.id}/billing-date`, { nextDueDate: c.values.date, confirmTenantCode: c.code, reason: c.reason }, { 'X-Reauth-Totp': c.totp }); render(); }
            catch (e) { alert(e.message); }
          } }, 'Mudar dia de cobrança'),
          t.status === 1
            ? h('button', { class: 'btn danger', onclick: () => critical(`/admin/v1/tenants/${t.id}/suspend`, 'Suspender empresa', true) }, 'Suspender')
            : h('button', { class: 'btn', onclick: () => critical(`/admin/v1/tenants/${t.id}/reactivate`, 'Reativar empresa', false) }, 'Reativar'))),
      h('div', { class: 'card' }, h('h2', {}, 'Lojas'), h('ul', {}, d.stores.map(s => h('li', {}, `${s.name} (${s.code})`))),
        h('h2', {}, 'Faturas'), d.invoices.length
          ? h('table', {}, h('tbody', {}, d.invoices.map(i => h('tr', {},
              h('td', {}, money(i.amountCents)), h('td', {}, 'vence ' + dt(i.dueAt)),
              h('td', {}, h('span', { class: 'pill ' + (i.status === 2 ? 'ok' : i.status === 3 ? '' : 'warn') }, ['', 'aberta', 'paga', 'cancelada'][i.status])),
              h('td', {}, invoiceActions(i, t))))))
          : h('p', { class: 'muted' }, 'Nenhuma fatura.'))),
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

// ---------- Armazenamento por empresa (pasta do Google Drive + credencial da API) ----------
async function tenantStorage(d) {
  const t = d.tenant;
  const base = `/admin/v1/tenants/${t.id}/storage`;
  const s = await call('GET', base);
  const goog = await call('GET', '/admin/v1/settings/google-oauth');
  const msg = h('div', { class: 'muted' });
  const err = h('div', { class: 'error' });
  const provider = h('select', {}, h('option', { value: 'local' }, 'Servidor (padrão)'), h('option', { value: 'gdrive' }, 'Google Drive da empresa'));
  // Conta Google pessoal (OAuth): abre o Google numa aba nova; ao autorizar, a pasta é criada e a configuração salva sozinha.
  const connectGoogle = async () => {
    err.textContent = '';
    const c = await confirmDialog({ title: 'Conectar conta Google', text: `As fotos de ${t.tradeName} passam a ser salvas numa pasta criada no Google Drive da conta que você autorizar. O sistema só enxerga os arquivos que ele mesmo criar.`, totp: true });
    if (!c) return;
    try {
      const r = await call('POST', base + '/google/start', {}, { 'X-Reauth-Totp': c.totp });
      window.open(r.url, '_blank', 'noopener');
      msg.textContent = 'Autorize na aba do Google que abriu. Depois volte aqui e clique em "Atualizar".';
    } catch (e) { err.textContent = e.message; }
  };
  const googleBox = h('div', { class: 'card', style: 'background:transparent;margin:0 0 12px 0' },
    h('h3', {}, 'Google Drive pessoal (conta Gmail)'),
    goog.configured
      ? [h('p', { class: 'muted' }, 'Funciona com Gmail grátis. Clique, escolha a sua conta Google e autorize: o sistema cria a pasta "Sistema Comercial - ', t.tradeName, '" no seu Drive.'),
         h('div', { class: 'row' }, h('button', { class: 'btn', onclick: connectGoogle }, s.provider === 'gdrive' && s.hasCredentials ? 'Conectar outra conta Google' : 'Conectar minha conta Google'),
           h('button', { class: 'btn secondary', onclick: () => render() }, 'Atualizar'))]
      : h('p', { class: 'muted' }, 'Para usar, configure primeiro o app do Google na aba "Google" (Client ID e segredo).'));
  provider.value = s.provider;
  const folder = h('input', { placeholder: 'Cole o link da pasta (https://drive.google.com/drive/folders/...) ou o ID', value: s.driveFolderId || '' });
  const impersonate = h('input', { type: 'email', placeholder: 'Opcional — só com delegação de domínio do Google Workspace', value: s.impersonateUser || '' });
  const creds = h('textarea', { rows: '5', placeholder: s.hasCredentials ? 'Já existe uma credencial salva. Cole um novo JSON somente para substituí-la.' : 'Cole aqui o conteúdo do arquivo JSON da conta de serviço', autocomplete: 'off', spellcheck: 'false' });
  const file = h('input', { type: 'file', accept: 'application/json,.json', onchange: async e => {
    const f = e.target.files[0]; if (!f) return;
    if (f.size > 20000) { err.textContent = 'Arquivo grande demais para uma credencial.'; return; }
    creds.value = await f.text(); msg.textContent = `Arquivo "${f.name}" carregado (ainda não salvo).`;
  } });
  const remove = h('input', { type: 'checkbox' });
  const gdriveBox = h('div', {},
    h('label', {}, 'Pasta do Google Drive onde os arquivos desta empresa serão salvos'), folder,
    h('label', {}, 'Credencial da API do Google (JSON da conta de serviço)'), file, creds,
    s.hasCredentials ? h('p', { class: 'muted' }, 'Credencial salva: ', h('strong', {}, s.credentialsClientEmail), ' — compartilhe a pasta com este e-mail como Editor.') : null,
    s.hasCredentials ? h('label', { class: 'row' }, remove, ' Remover a credencial salva') : null,
    h('label', {}, 'Usuário para delegação (opcional)'), impersonate);
  const sync = () => { gdriveBox.style.display = provider.value === 'gdrive' ? '' : 'none'; };
  provider.addEventListener('change', sync); sync();

  const status = s.lastTestAt
    ? h('p', {}, h('span', { class: 'pill ' + (s.lastTestOk ? 'ok' : 'bad') }, s.lastTestOk ? 'conexão ok' : 'falha'), ' ', s.lastTestMessage, h('span', { class: 'muted' }, ' • testado em ' + dt(s.lastTestAt)))
    : h('p', { class: 'muted' }, s.version ? 'Configuração ainda não testada.' : 'Usando o armazenamento padrão do servidor.');

  const save = async () => {
    err.textContent = '';
    const c = await confirmDialog({ title: 'Alterar armazenamento', text: `Os próximos arquivos de ${t.tradeName} serão salvos no destino escolhido. Arquivos já enviados continuam onde estão.`, tenantCode: t.code, totp: true });
    if (!c) return;
    try {
      await call('PUT', base, { provider: provider.value, driveFolder: folder.value, impersonateUser: impersonate.value || null,
        credentialsJson: creds.value.trim() || null, removeCredentials: remove.checked, confirmTenantCode: c.code }, { 'X-Reauth-Totp': c.totp });
      creds.value = '';
      render();
    } catch (e) { err.textContent = e.message; }
  };
  const test = async () => {
    err.textContent = ''; msg.textContent = 'Testando conexão...';
    try { const r = await call('POST', base + '/test', {}); msg.textContent = (r.lastTestOk ? '✔ ' : '✖ ') + r.lastTestMessage; } catch (e) { msg.textContent = ''; err.textContent = e.message; }
  };

  return [
    h('div', { class: 'card' }, h('h2', {}, 'Armazenamento de arquivos'),
      h('p', { class: 'muted' }, 'Define onde ficam as fotos de produtos desta empresa. A credencial é guardada criptografada e nunca é exibida de volta; o acesso aos arquivos é sempre feito pela API, que confere a empresa.'),
      status,
      s.provider === 'gdrive' && s.hasCredentials ? h('p', {}, 'Conta conectada: ', h('strong', {}, s.credentialsClientEmail)) : null,
      googleBox,
      h('details', {}, h('summary', {}, 'Configuração avançada (conta de serviço / Google Workspace)'),
        h('label', {}, 'Onde salvar'), provider, gdriveBox),
      h('div', { class: 'row', style: 'margin-top:12px' },
        h('button', { class: 'btn', onclick: save }, 'Salvar'),
        h('button', { class: 'btn secondary', onclick: test, disabled: !s.version }, 'Testar conexão')),
      msg, err),
    h('div', { class: 'card' }, h('h2', {}, 'Avançado: conta de serviço (só Google Workspace)'),
      h('p', { class: 'muted' }, 'Contas de serviço não têm espaço próprio: só gravam num Drive compartilhado do Google Workspace. Com Gmail grátis, use "Conectar minha conta Google".'),
      h('ol', {},
        h('li', {}, 'No Google Cloud Console, crie um projeto e ative a "Google Drive API".'),
        h('li', {}, 'Em "IAM e administrador → Contas de serviço", crie uma conta de serviço e gere uma chave do tipo JSON (baixa um arquivo).'),
        h('li', {}, 'No Google Drive, crie a pasta da empresa (de preferência num Drive compartilhado) e compartilhe com o e-mail da conta de serviço como Editor.'),
        h('li', {}, 'Aqui: escolha "Google Drive da empresa", cole o link da pasta, envie o arquivo JSON, salve e clique em "Testar conexão".'))),
  ];
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
  const imgUrl = id => id && logoUrls[id] ? logoUrls[id] : null;

  const colorFields = [['primaryColor', 'Primária'], ['secondaryColor', 'Secundária'], ['accentColor', 'Destaque'], ['backgroundColor', 'Fundo'], ['surfaceColor', 'Superfície'],
    ['textColor', 'Texto'], ['borderColor', 'Bordas'], ['darkBackgroundColor', 'Fundo (escuro)'], ['darkSurfaceColor', 'Superfície (escuro)'], ['darkTextColor', 'Texto (escuro)']];
  const textFields = [['systemName', 'Nome do sistema (aparece no caixa, no login e no comprovante)', 60], ['companyName', 'Nome da empresa', 120],
    ['welcomeMessage', 'Mensagem de boas-vindas (login)', 200], ['loginSubtitle', 'Texto abaixo das boas-vindas (login)', 120], ['tagline', 'Slogan abaixo do nome (login)', 60],
    ['headerSubtitle', 'Subtítulo no topo do caixa', 80], ['feature1', 'Destaque 1 (rodapé do login)', 40], ['feature2', 'Destaque 2', 40], ['feature3', 'Destaque 3', 40],
    ['reportHeader', 'Cabeçalho de relatórios', 200]];
  // Imagens da empresa: tipo enviado ao servidor → campo da configuração.
  const imageKinds = [
    ['logo', 'logoAssetId', 'Logo (fundo claro)', 'PNG com fundo transparente, ~600×200'],
    ['logo_dark', 'logoDarkAssetId', 'Logo para fundo escuro (topo do caixa e login)', 'Versão clara/branca da logo; se faltar, usa a logo normal'],
    ['icon', 'iconAssetId', 'Ícone (janela, barra de tarefas e cadeado do login)', 'Quadrado, PNG, 256×256'],
    ['login_image', 'loginImageAssetId', 'Ilustração do login (lado esquerdo)', 'Substitui o desenho do caixa; PNG transparente ~1000×650'],
    ['login_background', 'loginBackgroundAssetId', 'Imagem de fundo do login', 'Foto da loja ou da marca; JPEG 1920×1080 (fica com a cor da marca por cima)'],
    ['splash', 'splashAssetId', 'Tela de abertura (app do celular)', 'Opcional']];
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
    // Prévia da tela de login do caixa (como fica no computador da loja).
    const pdvLogin = (() => {
      const box = h('div', { class: 'pv' });
      box.style.display = 'grid'; box.style.gridTemplateColumns = '1.1fr 1fr'; box.style.minHeight = '230px'; box.style.position = 'relative';
      box.style.fontFamily = cfg.fontFamily + ', system-ui'; box.style.overflow = 'hidden';
      const left = h('div', {}); left.style.padding = '14px'; left.style.color = '#fff'; left.style.position = 'relative';
      left.style.background = `linear-gradient(135deg, ${cfg.primaryColor}cc, #000000dd)`;
      if (imgUrl(cfg.loginBackgroundAssetId)) { left.style.backgroundImage = `linear-gradient(135deg, ${cfg.primaryColor}99, #000000aa), url(${imgUrl(cfg.loginBackgroundAssetId)})`; left.style.backgroundSize = 'cover'; left.style.backgroundPosition = 'center'; }
      const logoDark = imgUrl(cfg.logoDarkAssetId) || imgUrl(cfg.logoAssetId);
      const brand = h('div', { style: 'display:flex;align-items:center;gap:8px' },
        logoDark ? h('img', { src: logoDark, alt: '', style: 'max-height:34px;max-width:90px' }) : h('span', { style: 'font-size:26px' }, '🛒'),
        h('div', {}, h('div', { style: 'font-weight:700;font-size:16px' }, cfg.systemName), h('div', { style: 'font-size:8px;letter-spacing:2px;opacity:.8' }, (cfg.tagline || '').toUpperCase())));
      const ill = imgUrl(cfg.loginImageAssetId) ? h('img', { src: imgUrl(cfg.loginImageAssetId), alt: '', style: 'max-width:100%;max-height:70px;margin:8px 0' }) : h('div', { style: 'height:50px;margin:8px 0;border-radius:6px;background:#ffffff22' });
      left.append(brand, h('div', { style: 'font-weight:700;margin-top:10px' }, cfg.welcomeMessage), h('div', { style: 'font-size:11px;opacity:.85' }, cfg.loginSubtitle || ''), ill,
        h('div', { style: 'display:flex;gap:8px;font-size:9px;opacity:.9' }, [cfg.feature1, cfg.feature2, cfg.feature3].map(f => h('span', {}, '✔ ' + (f || '')))));
      const right = h('div', {}); right.style.background = cfg.backgroundColor; right.style.display = 'flex'; right.style.alignItems = 'center'; right.style.justifyContent = 'center';
      const card = h('div', {}); card.style.background = cfg.surfaceColor; card.style.color = cfg.textColor; card.style.padding = '12px'; card.style.borderRadius = '10px'; card.style.width = '80%';
      const icon = imgUrl(cfg.iconAssetId);
      card.append(h('div', { style: 'display:flex;align-items:center;gap:8px;margin-bottom:8px' },
        icon ? h('img', { src: icon, alt: '', style: 'width:26px;height:26px' }) : h('span', {}, '🔒'), h('strong', {}, cfg.systemName)),
        h('div', { style: `height:12px;border:1px solid ${cfg.borderColor};border-radius:4px;margin:4px 0` }), h('div', { style: `height:12px;border:1px solid ${cfg.borderColor};border-radius:4px;margin:4px 0` }), btn('Entrar'));
      right.append(card);
      box.append(left, right);
      return h('div', {}, h('div', { class: 'muted', style: 'margin:4px 0' }, 'Login do caixa'), box);
    })();
    preview.replaceChildren(pdvLogin,
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
      const key = imageKinds.find(k => k[0] === kind)[1];
      cfg[key] = a.id; logoUrls[a.id] = await blobUrl(`${base}/assets/${a.id}`);
      msg.textContent = `Imagem enviada (${a.width}×${a.height}). Salve o rascunho e publique para aparecer nos caixas.`; renderPreview(); renderImages();
    } catch (e) { msg.textContent = 'Upload recusado: ' + e.message; }
  };
  for (const [, key] of imageKinds) if (cfg[key]) { try { logoUrls[cfg[key]] = await blobUrl(`${base}/assets/${cfg[key]}`); } catch { } }
  const imagesBox = h('div', {});
  const renderImages = () => imagesBox.replaceChildren(...imageKinds.map(([kind, key, label, help]) => h('div', { style: 'margin:10px 0;padding:8px;border:1px solid #ffffff22;border-radius:8px' },
    h('label', {}, label), h('div', { class: 'muted', style: 'font-size:12px' }, help + ' • PNG/JPEG até 2 MB'),
    h('div', { class: 'row', style: 'align-items:center;gap:8px;margin-top:6px' },
      imgUrl(cfg[key]) ? h('img', { src: imgUrl(cfg[key]), alt: '', style: 'max-height:48px;max-width:120px;background:#fff2;border-radius:4px' }) : h('span', { class: 'muted' }, 'padrão do produto'),
      h('input', { type: 'file', accept: 'image/png,image/jpeg', onchange: e => upload(kind, e.target) }),
      cfg[key] ? h('button', { class: 'btn secondary', onclick: () => { cfg[key] = null; renderPreview(); renderImages(); msg.textContent = 'Imagem removida do rascunho (volta ao padrão ao publicar).'; } }, 'Remover') : null))));
  renderImages();

  const save = async () => { try { await call('PUT', base + '/draft', cfg); msg.textContent = 'Rascunho salvo.'; } catch (e) { msg.textContent = e.message; } };
  const publish = async () => {
    await save();
    const c = await confirmDialog({ title: 'Publicar identidade visual', text: `A nova identidade será aplicada em todos os terminais e aplicativos de ${t.tradeName}.`, tenantCode: t.code });
    if (!c) return;
    try { const v = await call('POST', base + '/publish', { confirmTenantCode: c.code, note: 'publicado pelo painel' }); msg.textContent = 'Publicada versão ' + v.version; render(); } catch (e) { msg.textContent = e.message; }
  };

  const form = h('div', {},
    h('label', {}, 'Tema predefinido'),
    h('select', { onchange: e => { const p = presets.find(x => x.theme === e.target.value); if (!p) return; const keep = {}; for (const [k] of textFields) keep[k] = cfg[k]; for (const [, key] of imageKinds) keep[key] = cfg[key]; cfg = { ...p, ...keep }; Object.entries(inputs).forEach(([k, el]) => el.value = cfg[k] ?? ''); renderPreview(); } },
      h('option', { value: '' }, 'Aplicar tema...'), presets.map(p => h('option', { value: p.theme }, p.theme))),
    textFields.map(([k, l, max]) => [h('label', {}, l), bind(k, h('input', { maxlength: String(max) }))]),
    h('div', { class: 'grid', style: 'grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:8px' },
      colorFields.map(([k, l]) => h('div', {}, h('label', {}, l), bind(k, h('input', { type: 'color' }))))),
    h('div', { class: 'grid', style: 'grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:8px' },
      selects.map(([k, l, opts]) => h('div', {}, h('label', {}, l), bind(k, h('select', {}, opts.map(o => h('option', { value: o }, o))))))),
    h('label', {}, 'Escala do texto (0,85 a 1,3)'), bind('fontScale', h('input', { type: 'number', min: '0.85', max: '1.3', step: '0.05' }), Number),
    h('h3', {}, 'Imagens e ícones'), imagesBox,
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

// ---------- Acesso rápido à loja (sessão de suporte) ----------
const OPS = { 'products.write': 'cadastro de produtos', 'stock.adjust': 'ajuste de estoque', 'users.manage': 'usuários', 'devices.manage': 'terminais', 'sales.read': 'consulta de vendas' };
const canDo = op => state.support && state.support.mode === 2 && (state.support.allowedOperations || '').split(',').includes(op) && new Date(state.support.elevatedUntil) > new Date();

async function storeWorkspace(d) {
  const t = d.tenant;
  const active = state.support && state.support.tenantId === t.id && new Date(state.support.expiresAt) > new Date();
  if (!active) {
    state.support = null;
    const reason = h('input', { placeholder: 'Motivo do atendimento (mín. 10 caracteres) — o cliente é avisado' });
    const minutes = h('select', {}, [30, 60, 120].map(m => h('option', { value: m }, m + ' min')));
    const totp = h('input', { placeholder: 'Código do autenticador (MFA)', inputmode: 'numeric', maxlength: '6', autocomplete: 'one-time-code' });
    const err = h('div', { class: 'error' });
    const start = async () => {
      err.textContent = '';
      try {
        const s = await call('POST', `/admin/v1/tenants/${t.id}/support-sessions`, { reason: reason.value, durationMinutes: +minutes.value, totpCode: totp.value || null });
        state.support = { ...s, tenantId: t.id };
        render();
      } catch (e) { err.textContent = e.message; }
    };
    return h('div', { class: 'card' }, h('h2', {}, 'Acessar a loja para suporte'),
      h('p', { class: 'muted' }, 'Você entra na loja pelo painel, com prazo, e tudo fica na auditoria. O proprietário recebe um aviso. ',
        'Se ele marcou em "Suporte" o que você pode alterar sem pedir, informe o MFA e a sessão já começa com essas permissões; senão, começa em modo consulta e você pode pedir liberação.'),
      h('label', {}, 'Motivo'), reason, h('label', {}, 'Duração'), minutes, h('label', {}, 'MFA (para usar a autorização prévia do cliente)'), totp,
      h('div', { class: 'row', style: 'margin-top:12px' }, h('button', { class: 'btn', onclick: start }, 'Entrar na loja')), err);
  }

  const s = state.support;
  const msg = h('div', { class: 'muted' });
  const fail = e => { msg.textContent = e.code === 'support_scope' ? 'Sem permissão nesta sessão: peça liberação ao cliente (botão acima).' : e.message; };
  const allowed = (s.allowedOperations || '').split(',').filter(Boolean);
  const header = h('div', { class: 'card' },
    h('div', { class: 'row' },
      h('span', { class: 'pill ' + (s.mode === 2 ? 'ok' : 'warn') }, s.mode === 2 ? 'pode alterar' : 'somente consulta'),
      h('span', {}, s.mode === 2 ? 'Liberado: ' + allowed.map(o => OPS[o] || o).join(', ') : 'Alterações exigem aprovação do cliente.'),
      h('span', { class: 'muted' }, ' • até ' + dt(s.expiresAt)),
      h('div', { class: 'spacer' }),
      h('button', { class: 'btn secondary', onclick: async () => {
        const c = await confirmDialog({ title: 'Pedir liberação ao cliente', text: 'O proprietário recebe o pedido em Avisos/Suporte e decide. Válido por até 30 minutos.', totp: true, reason: true });
        if (!c) return;
        try { await call('POST', `/admin/v1/support-sessions/${s.id}/elevation-requests`, { reason: c.reason, operations: Object.keys(OPS).filter(o => o !== 'sales.read'), durationMinutes: 30, totpCode: c.totp }); msg.textContent = 'Pedido enviado ao cliente. Atualize esta tela depois que ele aprovar.'; }
        catch (e) { msg.textContent = e.message; }
      } }, 'Pedir liberação'),
      h('button', { class: 'btn secondary', onclick: async () => {
        // Recarrega o estado da sessão (ex.: cliente aprovou a liberação).
        try { const diag = await call('GET', `/admin/v1/support-sessions/${s.id}/diagnostics`); Object.assign(state.support, diag.session, { mode: diag.session.mode, allowedOperations: diag.session.allowedOperations, elevatedUntil: diag.session.elevatedUntil }); render(); }
        catch (e) { msg.textContent = e.message; }
      } }, 'Atualizar'),
      h('button', { class: 'btn danger', onclick: async () => { await call('POST', `/admin/v1/support-sessions/${s.id}/end`, {}); state.support = null; render(); } }, 'Encerrar acesso')),
    msg);

  // Produtos
  const q = h('input', { placeholder: 'Buscar produto (nome, SKU ou código de barras)' });
  const prodBody = h('tbody');
  const loadProducts = async () => {
    try {
      const list = await callStore('GET', '/api/v1/products?q=' + encodeURIComponent(q.value));
      prodBody.replaceChildren(...list.map(p => {
        const name = h('input', { value: p.name, disabled: !canDo('products.write') });
        const price = h('input', { value: (p.priceCents / 100).toFixed(2).replace('.', ','), style: 'width:110px', disabled: !canDo('products.write') });
        const cost = h('input', { value: (p.costCents / 100).toFixed(2).replace('.', ','), style: 'width:110px', disabled: !canDo('products.write') });
        const cents = v => Math.round(parseFloat(String(v).replace(/\./g, '').replace(',', '.')) * 100);
        return h('tr', {}, h('td', {}, name, h('div', { class: 'muted' }, p.sku + (p.barcode ? ' • ' + p.barcode : ''))), h('td', {}, price), h('td', {}, cost),
          h('td', {}, canDo('products.write') ? h('button', { class: 'btn secondary', onclick: async () => {
            try {
              await callStore('PUT', `/api/v1/products/${p.id}`, { sku: p.sku, barcode: p.barcode, name: name.value, description: p.description, unit: p.unit,
                priceCents: cents(price.value), costCents: cents(cost.value), minStock: p.minStock, categoryId: p.categoryId, expectedVersion: p.version,
                ncm: p.ncm, cest: p.cest, cfop: p.cfop, fiscalOrigin: p.fiscalOrigin, icmsCst: p.icmsCst, ibsCbsCst: p.ibsCbsCst, ibsCbsClass: p.ibsCbsClass });
              msg.textContent = `Produto "${name.value}" salvo.`; await loadProducts();
            } catch (e) { fail(e); }
          } }, 'Salvar') : h('span', { class: 'muted' }, 'consulta')));
      }));
    } catch (e) { fail(e); }
  };
  q.addEventListener('input', () => { clearTimeout(q._t); q._t = setTimeout(loadProducts, 300); });

  // Estoque
  const store = h('select', {}, d.stores.map(x => h('option', { value: x.id }, x.name)));
  const stockBody = h('tbody');
  const loadStock = async () => {
    try {
      const rows = await callStore('GET', '/api/v1/stock/balances?storeId=' + store.value);
      stockBody.replaceChildren(...rows.map(r => {
        const qty = h('input', { placeholder: '+10 ou -2', style: 'width:100px', disabled: !canDo('stock.adjust') });
        return h('tr', {}, h('td', {}, r.name, h('div', { class: 'muted' }, r.sku)), h('td', {}, String(r.quantity).replace('.', ',') + (r.low ? ' ⚠' : '')),
          h('td', {}, canDo('stock.adjust') ? h('div', { class: 'row' }, qty, h('button', { class: 'btn secondary', onclick: async () => {
            const n = parseFloat(qty.value.replace(',', '.'));
            if (!n) { msg.textContent = 'Informe a quantidade a somar ou subtrair.'; return; }
            const why = prompt('Motivo do ajuste (fica na auditoria):', 'Ajuste pelo suporte a pedido do cliente');
            if (!why) return;
            try { await callStore('POST', '/api/v1/stock/adjustments', { storeId: store.value, productId: r.productId, quantity: n, reason: why }); msg.textContent = 'Estoque ajustado.'; await loadStock(); }
            catch (e) { fail(e); }
          } }, 'Ajustar')) : h('span', { class: 'muted' }, 'consulta')));
      }));
    } catch (e) { fail(e); }
  };
  store.addEventListener('change', loadStock);

  // Usuários e terminais
  const usersBody = h('tbody'), devBody = h('tbody');
  const loadUsers = async () => {
    if (!canDo('users.manage')) { usersBody.replaceChildren(h('tr', {}, h('td', { class: 'muted' }, 'Requer liberação de "usuários".'))); return; }
    try {
      const list = await callStore('GET', '/api/v1/users');
      usersBody.replaceChildren(...list.map(u => h('tr', {}, h('td', {}, u.displayName, h('div', { class: 'muted' }, u.email)), h('td', {}, (u.roles || []).join(', ')),
        h('td', {}, u.isActive ? 'ativo' : 'bloqueado'),
        h('td', {}, h('div', { class: 'row' },
          h('button', { class: 'btn secondary', onclick: async () => { try { await callStore('POST', `/api/v1/users/${u.id}/${u.isActive ? 'block' : 'unblock'}`, {}); await loadUsers(); } catch (e) { fail(e); } } }, u.isActive ? 'Bloquear' : 'Desbloquear'),
          h('button', { class: 'btn secondary', onclick: async () => { try { await callStore('POST', `/api/v1/users/${u.id}/revoke-sessions`, {}); msg.textContent = 'Sessões encerradas: o usuário precisa entrar de novo.'; } catch (e) { fail(e); } } }, 'Deslogar'))))));
    } catch (e) { fail(e); }
  };
  const loadDevices = async () => {
    if (!canDo('devices.manage')) { devBody.replaceChildren(h('tr', {}, h('td', { class: 'muted' }, 'Requer liberação de "terminais". Veja a lista no Resumo.'))); return; }
    try {
      const list = await callStore('GET', '/api/v1/devices');
      devBody.replaceChildren(...list.map(x => h('tr', {}, h('td', {}, x.name), h('td', {}, x.platform), h('td', {}, ['', 'ativo', 'revogado'][x.status] || x.status), h('td', {}, dt(x.lastSyncAt)),
        h('td', {}, x.status === 1 ? h('button', { class: 'btn danger', onclick: async () => { if (!confirm('Revogar este terminal? Ele precisará ser ativado de novo.')) return; try { await callStore('POST', `/api/v1/devices/${x.id}/revoke`, {}); await loadDevices(); } catch (e) { fail(e); } } }, 'Revogar') : null))));
    } catch (e) { fail(e); }
  };

  await Promise.all([loadProducts(), loadStock(), loadUsers(), loadDevices()]);
  return [header,
    h('div', { class: 'card' }, h('h2', {}, 'Produtos'), q, h('table', {}, h('thead', {}, h('tr', {}, ['Produto', 'Preço (R$)', 'Custo (R$)', ''].map(x => h('th', {}, x)))), prodBody)),
    h('div', { class: 'card' }, h('h2', {}, 'Estoque'), store, h('table', {}, h('thead', {}, h('tr', {}, ['Produto', 'Saldo', 'Ajuste'].map(x => h('th', {}, x)))), stockBody)),
    h('div', { class: 'grid' },
      h('div', { class: 'card' }, h('h2', {}, 'Usuários'), h('table', {}, h('tbody', {}, usersBody))),
      h('div', { class: 'card' }, h('h2', {}, 'Terminais'), h('table', {}, h('tbody', {}, devBody)))),
  ];
}

// ---------- Faturas de todas as empresas ----------
async function invoicesView() {
  const rows = await call('GET', '/admin/v1/invoices/upcoming?days=' + state.invoiceDays);
  const total = rows.reduce((s, r) => s + r.amountCents, 0);
  const msg = h('div', { class: 'muted' });
  const pick = h('select', { onchange: e => { state.invoiceDays = +e.target.value; render(); } },
    [[3, 'Vencem nos próximos 3 dias'], [7, 'Próximos 7 dias'], [30, 'Próximos 30 dias']].map(([v, l]) => h('option', { value: v }, l)));
  pick.value = state.invoiceDays;
  return [
    h('div', { class: 'card' },
      h('div', { class: 'row' }, h('h2', { style: 'margin:0' }, 'Faturas a vencer e vencidas'), h('div', { class: 'spacer' }), pick,
        h('button', { class: 'btn secondary', onclick: async () => { try { const r = await call('POST', '/admin/v1/billing/run', {}); msg.textContent = `${r.created} fatura(s) gerada(s), ${r.reminders} lembrete(s) enviado(s).`; setTimeout(render, 1200); } catch (e) { msg.textContent = e.message; } } }, 'Gerar faturas agora')),
      h('p', { class: 'muted' }, 'As faturas são geradas automaticamente 10 dias antes do fim de cada período e o cliente recebe lembrete 3 dias antes do vencimento. ',
        `${rows.length} fatura(s) em aberto nesta janela • total ${money(total)}.`),
      msg),
    h('div', { class: 'card' }, rows.length === 0 ? h('p', { class: 'muted' }, 'Nenhuma fatura em aberto nesta janela.') :
      h('table', {}, h('thead', {}, h('tr', {}, ['Empresa', 'Valor', 'Vencimento', 'Situação', 'Pagamento iniciado', ''].map(x => h('th', {}, x)))),
        h('tbody', {}, rows.map(r => h('tr', {},
          h('td', {}, h('a', { href: '#', onclick: e => { e.preventDefault(); openTenant(r.tenantId); } }, r.tradeName), h('div', { class: 'muted' }, r.code)),
          h('td', {}, money(r.amountCents)), h('td', {}, dt(r.dueAt)),
          h('td', {}, h('span', { class: 'pill ' + (r.overdue ? 'bad' : r.daysToDue <= 3 ? 'warn' : '') }, r.overdue ? `vencida há ${-r.daysToDue} dia(s)` : r.daysToDue <= 0 ? 'vence hoje' : `vence em ${r.daysToDue} dia(s)`)),
          h('td', {}, [r.hasPix ? 'PIX gerado' : null, r.hasCheckout ? 'link cartão/boleto' : null].filter(Boolean).join(' • ') || '—', r.reminderSentAt ? h('div', { class: 'muted' }, 'lembrete enviado') : null),
          h('td', {}, invoiceActions({ ...r, status: 1 }, { tradeName: r.tradeName, code: r.code })))))))
  ];
}

// ---------- Mercado Pago ----------
async function paymentsView() {
  const s = await call('GET', '/admin/v1/settings/mercadopago');
  const msg = h('div', { class: 'muted' }), err = h('div', { class: 'error' });
  const token = h('input', { type: 'password', autocomplete: 'off', placeholder: s.configured ? `Configurado (${s.accessTokenHint}) — preencha só para trocar` : 'APP_USR-... (produção) ou TEST-... (teste)' });
  const pub = h('input', { value: s.publicKey || '', placeholder: 'APP_USR-... (opcional)' });
  const secret = h('input', { type: 'password', autocomplete: 'off', placeholder: s.hasWebhookSecret ? 'Configurada — preencha só para trocar' : 'Assinatura secreta dos webhooks (painel do Mercado Pago)' });
  const baseUrl = h('input', { value: s.notificationBaseUrl || '', placeholder: 'https://api.suaempresa.com.br (endereço público desta API)' });
  const save = async () => {
    err.textContent = '';
    const c = await confirmDialog({ title: 'Salvar credenciais do Mercado Pago', text: 'Os pagamentos das mensalidades de todas as empresas passam a usar esta conta.', totp: true });
    if (!c) return;
    try {
      await call('PUT', '/admin/v1/settings/mercadopago', { accessToken: token.value || null, publicKey: pub.value, webhookSecret: secret.value || null, notificationBaseUrl: baseUrl.value }, { 'X-Reauth-Totp': c.totp });
      render();
    } catch (e) { err.textContent = e.message; }
  };
  return [
    h('div', { class: 'card' }, h('h2', {}, 'Mercado Pago — recebimento das mensalidades'),
      h('p', {}, 'Situação: ', h('span', { class: 'pill ' + (s.configured ? 'ok' : 'bad') }, s.configured ? (s.isTestToken ? 'configurado (modo TESTE)' : 'configurado (produção)') : 'não configurado'),
        s.updatedAt ? h('span', { class: 'muted' }, ' • alterado em ' + dt(s.updatedAt)) : null),
      h('p', { class: 'muted' }, 'Os clientes pagam por PIX (QR Code no próprio sistema) ou por cartão e boleto (página segura do Mercado Pago). Os dados de cartão nunca passam pelo nosso sistema. O Access Token e a assinatura secreta ficam criptografados e não são exibidos de volta.'),
      h('label', {}, 'Access Token'), token,
      h('label', {}, 'Public Key'), pub,
      h('label', {}, 'Assinatura secreta do webhook'), secret,
      h('label', {}, 'Endereço público desta API (para o Mercado Pago avisar os pagamentos)'), baseUrl,
      h('p', { class: 'muted' }, 'Cadastre no painel do Mercado Pago (Suas integrações → Webhooks → evento "Pagamentos") a URL: ', h('strong', {}, s.webhookUrl)),
      h('div', { class: 'row', style: 'margin-top:12px' },
        h('button', { class: 'btn', onclick: save }, 'Salvar'),
        h('button', { class: 'btn secondary', onclick: async () => { msg.textContent = 'Testando...'; try { const r = await call('POST', '/admin/v1/settings/mercadopago/test', {}); msg.textContent = (r.ok ? '✔ ' : '✖ ') + r.message; } catch (e) { msg.textContent = e.message; } } }, 'Testar credencial')),
      msg, err),
    h('div', { class: 'card' }, h('h2', {}, 'Como obter as credenciais'),
      h('ol', {},
        h('li', {}, 'Entre em mercadopago.com.br/developers → "Suas integrações" → crie uma aplicação (pagamentos online).'),
        h('li', {}, 'Em "Credenciais de produção" copie o Access Token e a Public Key. Para testar sem cobrar, use as "Credenciais de teste" (começam com TEST-).'),
        h('li', {}, 'Em "Webhooks", cadastre a URL acima, marque o evento Pagamentos e copie a "assinatura secreta".'),
        h('li', {}, 'Sem endereço público (ex.: rodando só no seu computador) o aviso automático não chega: o cliente usa o botão "Já paguei" e o sistema consulta o Mercado Pago.'))),
  ];
}

// ---------- E-mail (SMTP) ----------
async function emailView() {
  const s = await call('GET', '/admin/v1/settings/smtp');
  const list = await call('GET', '/admin/v1/emails');
  const msg = h('div', { class: 'muted' }), err = h('div', { class: 'error' });
  const host = h('input', { value: s.host || '', placeholder: 'smtp.gmail.com, smtp-relay.brevo.com, smtp.office365.com...' });
  const port = h('input', { value: s.port || 587, type: 'number', style: 'width:120px' });
  const ssl = h('input', { type: 'checkbox' }); ssl.checked = s.ssl !== false;
  const user = h('input', { value: s.user || '', autocomplete: 'off' });
  const pass = h('input', { type: 'password', autocomplete: 'new-password', placeholder: s.hasPassword ? 'Configurada — preencha só para trocar' : 'Senha ou senha de app do e-mail' });
  const from = h('input', { value: s.from || '', placeholder: 'nao-responda@seudominio.com.br ou seu Gmail' });
  const fromName = h('input', { value: s.fromName || '', placeholder: 'Nome que aparece para o cliente' });
  const to = h('input', { type: 'email', placeholder: 'Seu e-mail para receber o teste' });
  const save = async () => {
    err.textContent = '';
    const c = await confirmDialog({ title: 'Salvar configuração de e-mail', text: 'Todos os e-mails do sistema (senhas, faturas, boas-vindas) passam a sair por este servidor.', totp: true });
    if (!c) return;
    try { await call('PUT', '/admin/v1/settings/smtp', { host: host.value, port: +port.value, ssl: ssl.checked, user: user.value, password: pass.value || null, from: from.value, fromName: fromName.value }, { 'X-Reauth-Totp': c.totp }); render(); }
    catch (e) { err.textContent = e.message; }
  };
  return [
    h('div', { class: 'card' }, h('h2', {}, 'E-mail do sistema'),
      h('p', {}, 'Situação: ', h('span', { class: 'pill ' + (s.configured ? 'ok' : 'warn') }, s.configured ? 'configurado' : 'não configurado (em desenvolvimento os e-mails viram arquivos .eml em dev-data/emails)')),
      h('p', { class: 'muted' }, 'Usado para "esqueci minha senha", boas-vindas de funcionários e avisos de fatura. Com CPF você pode usar um Gmail com "senha de app" (até ~500 e-mails/dia) ou um serviço como Brevo (300/dia grátis).'),
      h('div', { class: 'grid' },
        h('div', {}, h('label', {}, 'Servidor SMTP'), host, h('label', {}, 'Porta'), port, h('label', { class: 'row' }, ssl, ' Usar TLS/SSL (recomendado)')),
        h('div', {}, h('label', {}, 'Usuário'), user, h('label', {}, 'Senha'), pass, h('label', {}, 'E-mail do remetente'), from, h('label', {}, 'Nome do remetente'), fromName)),
      h('div', { class: 'row', style: 'margin-top:12px' }, h('button', { class: 'btn', onclick: save }, 'Salvar'), to,
        h('button', { class: 'btn secondary', onclick: async () => { msg.textContent = 'Enviando...'; try { const r = await call('POST', '/admin/v1/settings/smtp/test', { to: to.value }); msg.textContent = (r.ok ? '✔ ' : '✖ ') + r.message; } catch (e) { msg.textContent = e.message; } } }, 'Enviar teste')),
      msg, err),
    h('div', { class: 'card' }, h('h2', {}, 'Últimos e-mails'),
      list.length === 0 ? h('p', { class: 'muted' }, 'Nenhum e-mail ainda.') :
      h('table', {}, h('thead', {}, h('tr', {}, ['Quando', 'Para', 'Assunto', 'Tipo', 'Situação'].map(x => h('th', {}, x)))),
        h('tbody', {}, list.map(m => h('tr', {}, h('td', {}, dt(m.createdAt)), h('td', {}, m.to), h('td', {}, m.subject), h('td', {}, m.kind),
          h('td', {}, h('span', { class: 'pill ' + (m.status === 'sent' ? 'ok' : m.status === 'failed' ? 'bad' : 'warn') }, m.status === 'sent' ? 'enviado' : m.status === 'failed' ? 'falhou' : `na fila (${m.attempts} tentativa(s))`),
            m.lastError ? h('div', { class: 'muted' }, m.lastError) : null)))))),
  ];
}

// ---------- WhatsApp de atendimento (botão flutuante no caixa) ----------
async function contactView() {
  const s = await call('GET', '/admin/v1/settings/support');
  const err = h('div', { class: 'error' });
  const num = h('input', { value: s.whatsapp || '', placeholder: '(47) 99284-1812', style: 'width:240px' });
  const msg = h('input', { value: s.message || '', placeholder: 'Mensagem que já vem escrita no WhatsApp' });
  const fmt = n => n && n.length >= 12 ? `+${n.slice(0, 2)} (${n.slice(2, 4)}) ${n.slice(4, -4)}-${n.slice(-4)}` : n;
  const save = async (off) => {
    err.textContent = '';
    const c = await confirmDialog({ title: off ? 'Desligar botão do WhatsApp' : 'Salvar número do WhatsApp', text: off ? 'O botão some dos caixas no próximo início.' : 'Os caixas passam a abrir conversa com este número.', totp: true });
    if (!c) return;
    try { await call('PUT', '/admin/v1/settings/support', { whatsapp: off ? '' : num.value, message: msg.value }, { 'X-Reauth-Totp': c.totp }); render(); }
    catch (e) { err.textContent = e.message; }
  };
  return h('div', { class: 'card' }, h('h2', {}, 'WhatsApp de atendimento'),
    h('p', {}, 'Situação: ', h('span', { class: 'pill ' + (s.enabled ? 'ok' : 'warn') }, s.enabled ? 'ativo • ' + fmt(s.whatsapp) : 'desligado')),
    h('p', { class: 'muted' }, 'Número do botão verde flutuante que aparece no caixa (também na tela de login). Ao clicar, abre o WhatsApp com a mensagem abaixo, o nome da loja e o usuário. Os caixas atualizam o número ao abrir o programa.'),
    h('div', { class: 'grid' }, h('div', {}, h('label', {}, 'Número (DDD + número)'), num), h('div', {}, h('label', {}, 'Mensagem inicial'), msg)),
    h('div', { class: 'row', style: 'margin-top:12px' }, h('button', { class: 'btn', onclick: () => save(false) }, 'Salvar'),
      s.enabled ? h('button', { class: 'btn secondary', onclick: () => save(true) }, 'Desligar botão') : null,
      s.enabled ? h('a', { class: 'btn secondary', href: 'https://wa.me/' + s.whatsapp, target: '_blank', rel: 'noopener noreferrer' }, 'Testar') : null), err);
}

// ---------- Minha conta (e-mail e senha do administrador) ----------
async function accountView() {
  const me = state.me || await call('GET', '/admin/v1/me');
  const errE = h('div', { class: 'error' }), errP = h('div', { class: 'error' });
  const email = h('input', { type: 'email', value: me.email, autocomplete: 'off' });
  const curE = h('input', { type: 'password', autocomplete: 'current-password', placeholder: 'Senha atual' });
  const curP = h('input', { type: 'password', autocomplete: 'current-password', placeholder: 'Senha atual' });
  const nw = h('input', { type: 'password', autocomplete: 'new-password', placeholder: '10+ caracteres com letras e números' });
  const nw2 = h('input', { type: 'password', autocomplete: 'new-password', placeholder: 'Repita a nova senha' });
  const done = (what) => logout(`${what} alterado(a). Entre de novo com os dados novos (as outras sessões foram encerradas).`);
  const saveEmail = async () => {
    errE.textContent = '';
    const c = await confirmDialog({ title: 'Trocar e-mail de login', text: `O novo e-mail de login será ${email.value}.`, totp: true });
    if (!c) return;
    try { await call('PUT', '/admin/v1/me/email', { newEmail: email.value, currentPassword: curE.value }, { 'X-Reauth-Totp': c.totp }); done('E-mail'); }
    catch (e) { errE.textContent = e.message; }
  };
  const savePassword = async () => {
    errP.textContent = '';
    if (nw.value !== nw2.value) { errP.textContent = 'As duas senhas novas não são iguais.'; return; }
    const c = await confirmDialog({ title: 'Trocar senha', text: 'Depois da troca você entra de novo com a senha nova.', totp: true });
    if (!c) return;
    try { await call('PUT', '/admin/v1/me/password', { currentPassword: curP.value, newPassword: nw.value }, { 'X-Reauth-Totp': c.totp }); done('Senha'); }
    catch (e) { errP.textContent = e.message; }
  };
  return [
    h('div', { class: 'card' }, h('h2', {}, 'Trocar e-mail de login'),
      h('label', {}, 'Novo e-mail'), email, h('label', {}, 'Senha atual'), curE,
      h('div', { class: 'row', style: 'margin-top:12px' }, h('button', { class: 'btn', onclick: saveEmail }, 'Salvar e-mail')), errE),
    h('div', { class: 'card' }, h('h2', {}, 'Trocar senha'),
      h('label', {}, 'Senha atual'), curP, h('label', {}, 'Nova senha'), nw, h('label', {}, 'Repita a nova senha'), nw2,
      h('div', { class: 'row', style: 'margin-top:12px' }, h('button', { class: 'btn', onclick: savePassword }, 'Salvar senha')), errP),
    h('div', { class: 'card' }, h('p', { class: 'muted' }, 'As duas trocas pedem a senha atual e o código do autenticador, e encerram as outras sessões abertas do painel. O código do autenticador continua o mesmo.')),
  ];
}

// ---------- App do Google (OAuth) para conectar o Google Drive pessoal ----------
async function googleView() {
  const s = await call('GET', '/admin/v1/settings/google-oauth');
  const err = h('div', { class: 'error' });
  const id = h('input', { value: s.clientId || '', placeholder: '1234567890-xxxx.apps.googleusercontent.com' });
  const secret = h('input', { type: 'password', autocomplete: 'new-password', placeholder: s.hasSecret ? 'Configurado — preencha só para trocar' : 'GOCSPX-...' });
  const redirect = h('input', { value: s.redirectUri, readonly: true, onclick: e => e.target.select() });
  const save = async () => {
    err.textContent = '';
    const c = await confirmDialog({ title: 'Salvar app do Google', text: 'O segredo fica guardado criptografado e nunca é exibido de volta.', totp: true });
    if (!c) return;
    try { await call('PUT', '/admin/v1/settings/google-oauth', { clientId: id.value, clientSecret: secret.value || null }, { 'X-Reauth-Totp': c.totp }); render(); }
    catch (e) { err.textContent = e.message; }
  };
  return [
    h('div', { class: 'card' }, h('h2', {}, 'App do Google (Google Drive pessoal)'),
      h('p', {}, 'Situação: ', h('span', { class: 'pill ' + (s.configured ? 'ok' : 'warn') }, s.configured ? 'configurado' : 'não configurado')),
      h('label', {}, 'Endereço de retorno (copie e cadastre no Google)'), redirect,
      h('label', {}, 'Client ID'), id, h('label', {}, 'Segredo do cliente (Client secret)'), secret,
      h('div', { class: 'row', style: 'margin-top:12px' }, h('button', { class: 'btn', onclick: save }, 'Salvar')), err),
    h('div', { class: 'card' }, h('h2', {}, 'Passo a passo (uma vez só)'),
      h('ol', {},
        h('li', {}, 'Entre em console.cloud.google.com com a sua conta Google e crie um projeto (ex.: "Sistema Comercial").'),
        h('li', {}, 'APIs e serviços → Biblioteca → procure "Google Drive API" → Ativar.'),
        h('li', {}, 'APIs e serviços → Tela de permissão OAuth (Google Auth Platform): tipo "Externo", nome do app, seu e-mail de suporte e de contato. Em "Público", clique em "Publicar app" (com o app em teste a conexão expira em 7 dias). Os escopos usados (e-mail e drive.file) não exigem verificação do Google.'),
        h('li', {}, 'APIs e serviços → Credenciais → Criar credenciais → ID do cliente OAuth → tipo "Aplicativo da Web". Em "URIs de redirecionamento autorizados" cole o endereço de retorno acima.'),
        h('li', {}, 'Copie o Client ID e o segredo (Client secret) para os campos acima e salve.'),
        h('li', {}, 'Empresas → abra a empresa → Armazenamento → "Conectar minha conta Google".')),
      h('p', { class: 'muted' }, 'Em produção o endereço de retorno muda para https://api.seudominio.com.br/oauth/google/callback: cadastre os dois no Google.')),
  ];
}

// ---------- Versão do caixa (atualização automática) ----------
async function mobileReleaseCard() {
  const r = await call('GET', '/admin/v1/settings/mobile-release');
  const err = h('div', { class: 'error' });
  const code = h('input', { type: 'number', min: 1, value: r?.versionCode || '', placeholder: '2', style: 'width:120px' });
  const name = h('input', { value: r?.versionName || '', placeholder: '1.0.1', style: 'width:140px' });
  const url = h('input', { value: r?.url || '', placeholder: 'https://github.com/.../comercial-1.0.1.apk' });
  const notes = h('input', { value: r?.notes || '', placeholder: 'O que mudou (aparece no aviso do app)' });
  const mandatory = h('input', { type: 'checkbox' }); mandatory.checked = !!r?.mandatory;
  const save = async () => {
    err.textContent = '';
    const c = await confirmDialog({ title: 'Publicar app ' + name.value, text: 'Os celulares com versão menor passam a mostrar "Nova versão" ao abrir o app.', totp: true });
    if (!c) return;
    try { await call('PUT', '/admin/v1/settings/mobile-release', { versionCode: +code.value, versionName: name.value, url: url.value, notes: notes.value, mandatory: mandatory.checked }, { 'X-Reauth-Totp': c.totp }); render(); }
    catch (e) { err.textContent = e.message; }
  };
  return h('div', { class: 'card' }, h('h2', {}, 'App Android (celular do dono)'),
    h('p', {}, 'Versão publicada: ', h('span', { class: 'pill ' + (r ? 'ok' : 'warn') }, r ? `${r.versionName} (código ${r.versionCode})` : 'nenhuma')),
    h('p', { class: 'muted' }, 'O jeito fácil: rode dev/build-apk.ps1 -Publish -Notes "..." — ele gera o APK, sobe a versão e publica aqui sozinho. Para produção, suba o APK em HTTPS (ex.: GitHub Releases) e preencha abaixo. O código da versão precisa ser maior que o do app instalado.'),
    h('div', { class: 'grid' }, h('div', {}, h('label', {}, 'Código da versão (versionCode)'), code, h('label', {}, 'Versão'), name, h('label', {}, 'Endereço do APK'), url),
      h('div', {}, h('label', {}, 'Novidades'), notes, h('label', { class: 'row' }, mandatory, ' Obrigatória'))),
    h('div', { class: 'row', style: 'margin-top:12px' }, h('button', { class: 'btn', onclick: save }, 'Publicar versão do app')), err);
}

async function releasesView() {
  const mobile = await mobileReleaseCard();
  const r = await call('GET', '/admin/v1/settings/desktop-release');
  const err = h('div', { class: 'error' });
  const ver = h('input', { value: r?.version || '', placeholder: '1.0.1', style: 'width:140px' });
  const url = h('input', { value: r?.url || '', placeholder: 'https://github.com/SEU-USUARIO/SEU-REPO/releases/download/v1.0.1/Comercial-1.0.1-setup.exe' });
  const sha = h('input', { value: r?.sha256 || '', placeholder: 'SHA-256 mostrado pelo dev/publish-pdv.ps1', style: 'font-family:monospace' });
  const notes = h('input', { value: r?.notes || '', placeholder: 'O que mudou (aparece no aviso do caixa)' });
  const mandatory = h('input', { type: 'checkbox' }); mandatory.checked = !!r?.mandatory;
  const save = async () => {
    err.textContent = '';
    const c = await confirmDialog({ title: 'Publicar versão ' + ver.value, text: 'Todos os caixas vão oferecer esta atualização no próximo login. Confira o SHA-256: se não bater com o arquivo, o caixa recusa a instalação.', totp: true });
    if (!c) return;
    try { await call('PUT', '/admin/v1/settings/desktop-release', { version: ver.value, url: url.value, sha256: sha.value, notes: notes.value, mandatory: mandatory.checked }, { 'X-Reauth-Totp': c.totp }); render(); }
    catch (e) { err.textContent = e.message; }
  };
  return [mobile, h('div', { class: 'card' }, h('h2', {}, 'Atualização dos caixas'),
    h('p', {}, 'Versão publicada: ', h('span', { class: 'pill ' + (r ? 'ok' : 'warn') }, r ? r.version : 'nenhuma')),
    h('p', { class: 'muted' }, '1) Rode dev/publish-pdv.ps1 (gera o instalador e mostra o SHA-256). 2) Suba o .exe num endereço HTTPS (ex.: GitHub Releases). 3) Preencha abaixo. Os dados são assinados com a chave das licenças e o caixa confere o arquivo antes de instalar.'),
    h('div', { class: 'grid' }, h('div', {}, h('label', {}, 'Versão'), ver, h('label', {}, 'Endereço do instalador (HTTPS)'), url),
      h('div', {}, h('label', {}, 'SHA-256 do instalador'), sha, h('label', {}, 'Novidades'), notes, h('label', { class: 'row' }, mandatory, ' Obrigatória (instala mesmo com vendas na fila)'))),
    h('div', { class: 'row', style: 'margin-top:12px' }, h('button', { class: 'btn', onclick: save }, 'Publicar versão')), err)];
}

// ---------- NFS-e das mensalidades ----------
async function nfseView() {
  const s = await call('GET', '/admin/v1/settings/nfse');
  const list = await call('GET', '/admin/v1/nfse');
  const err = h('div', { class: 'error' }), msg = h('div', { class: 'muted' });
  const enabled = h('input', { type: 'checkbox' }); enabled.checked = s.enabled;
  const cnpj = h('input', { value: s.cnpj || '', placeholder: 'CNPJ da sua empresa (não aceita CPF)' });
  const im = h('input', { value: s.municipalRegistration || '', placeholder: 'Inscrição municipal' });
  const city = h('input', { value: s.cityCode || '', placeholder: 'Código IBGE do município (7 dígitos)' });
  const item = h('input', { value: s.serviceItem || '01.07', placeholder: 'Item da lista de serviços (LC 116)' });
  const rate = h('input', { value: s.issRate || '2.00', type: 'number', step: '0.01' });
  const token = h('input', { type: 'password', autocomplete: 'off', placeholder: s.hasToken ? 'Configurado — preencha só para trocar' : 'Token do Focus NFe (NFS-e)' });
  const env = h('select', {}, h('option', { value: 2 }, 'Homologação (testes)'), h('option', { value: 1 }, 'Produção')); env.value = s.environment;
  const save = async () => {
    err.textContent = '';
    const c = await confirmDialog({ title: 'Salvar NFS-e', text: 'Com a NFS-e ligada, toda fatura paga gera nota fiscal de serviço automaticamente.', totp: true });
    if (!c) return;
    try { await call('PUT', '/admin/v1/settings/nfse', { enabled: enabled.checked, cnpj: cnpj.value, municipalRegistration: im.value, cityCode: city.value, serviceItem: item.value, issRate: +rate.value, token: token.value || null, environment: +env.value }, { 'X-Reauth-Totp': c.totp }); render(); }
    catch (e) { err.textContent = e.message; }
  };
  return [
    h('div', { class: 'card' }, h('h2', {}, 'Nota fiscal de serviço (NFS-e) das mensalidades'),
      h('p', {}, 'Situação: ', h('span', { class: 'pill ' + (s.ready ? 'ok' : 'warn') }, s.ready ? 'ligada' : 'desligada')),
      h('p', { class: 'muted' }, 'Exige CNPJ e inscrição municipal. Operando por CPF, deixe desligada; quando abrir a empresa, preencha aqui e ligue. Confirme com seu contador o item da lista de serviços e a alíquota do ISS do seu município.'),
      h('div', { class: 'grid' },
        h('div', {}, h('label', {}, 'CNPJ'), cnpj, h('label', {}, 'Inscrição municipal'), im, h('label', {}, 'Município (IBGE)'), city),
        h('div', {}, h('label', {}, 'Item de serviço'), item, h('label', {}, 'Alíquota ISS (%)'), rate, h('label', {}, 'Token do provedor'), token, h('label', {}, 'Ambiente'), env)),
      h('label', { class: 'row' }, enabled, ' Emitir NFS-e automaticamente quando a fatura for paga'),
      h('div', { class: 'row', style: 'margin-top:12px' }, h('button', { class: 'btn', onclick: save }, 'Salvar'),
        h('button', { class: 'btn secondary', onclick: async () => { try { const r = await call('POST', '/admin/v1/nfse/run', {}); msg.textContent = `${r.processed} nota(s) processada(s).`; } catch (e) { msg.textContent = e.message; } } }, 'Processar fila agora')),
      msg, err),
    h('div', { class: 'card' }, h('h2', {}, 'Notas emitidas'),
      list.length === 0 ? h('p', { class: 'muted' }, 'Nenhuma NFS-e ainda.') :
      h('table', {}, h('thead', {}, h('tr', {}, ['Quando', 'Valor', 'Situação', 'Número', 'Mensagem'].map(x => h('th', {}, x)))),
        h('tbody', {}, list.map(n => h('tr', {}, h('td', {}, dt(n.createdAt)), h('td', {}, money(n.amountCents)),
          h('td', {}, h('span', { class: 'pill ' + (n.status === 'issued' ? 'ok' : n.status === 'error' ? 'bad' : 'warn') }, n.status)),
          h('td', {}, n.number || '—', n.pdfUrl ? [' ', h('a', { href: n.pdfUrl, target: '_blank', rel: 'noopener' }, 'ver')] : null), h('td', {}, n.lastMessage || ''))))))
  ];
}

async function plansView() {
  const plans = await call('GET', '/admin/v1/plans');
  const su = await call('GET', '/admin/v1/settings/signup');
  const err = h('div', { class: 'error' });
  const enabled = h('input', { type: 'checkbox' }); enabled.checked = su.enabled;
  const plan = h('select', {}, plans.map(p => { const o = h('option', { value: p.code }, p.name); if (p.code === su.planCode) o.selected = true; return o; }));
  const days = h('input', { type: 'number', min: 0, max: 90, value: su.trialDays, style: 'width:120px' });
  const saveSignup = async () => {
    err.textContent = '';
    const c = await confirmDialog({ title: 'Salvar cadastro com teste grátis', text: 'Vale para os próximos cadastros feitos pelo aplicativo do caixa.', totp: true });
    if (!c) return;
    try { await call('PUT', '/admin/v1/settings/signup', { enabled: enabled.checked, planCode: plan.value, trialDays: +days.value }, { 'X-Reauth-Totp': c.totp }); render(); }
    catch (e) { err.textContent = e.message; }
  };
  const signupCard = h('div', { class: 'card' }, h('h2', {}, 'Cadastro com teste grátis'),
    h('p', { class: 'muted' }, 'Lojistas podem criar a própria conta pelo caixa ("Criar conta grátis"): recebem um código no e-mail e a loja é criada com o plano e os dias de teste abaixo. A primeira fatura é gerada antes do fim do teste. Exige o e-mail do sistema configurado.'),
    h('div', { class: 'row' }, h('label', { class: 'row' }, enabled, ' Permitir cadastro pelo aplicativo')),
    h('div', { class: 'grid' }, h('div', {}, h('label', {}, 'Plano do teste'), plan), h('div', {}, h('label', {}, 'Dias de teste grátis (0 a 90)'), days)),
    h('div', { class: 'row', style: 'margin-top:12px' }, h('button', { class: 'btn', onclick: saveSignup }, 'Salvar')), err);
  return [signupCard, h('div', { class: 'card' }, h('h2', {}, 'Planos'),
    h('p', { class: 'muted' }, 'Alterações em planos não modificam assinaturas existentes (cada contrato guarda o preço e os limites da contratação).'),
    h('table', {}, h('thead', {}, h('tr', {}, ['Plano', 'Preço', 'Limites', 'Módulos'].map(x => h('th', {}, x)))),
      h('tbody', {}, plans.map(p => h('tr', {}, h('td', {}, h('strong', {}, p.name), h('div', { class: 'muted' }, p.code)), h('td', {}, money(p.priceCents) + (p.period === 12 ? '/ano' : '/mês')),
        h('td', {}, `${p.maxUsers} usuários • ${p.maxDevices} terminais • ${p.maxStores} lojas • ${p.graceDays} dias de tolerância`),
        h('td', {}, p.modules.map(m => m.moduleKey).join(', ')))))))];
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
  const nav = [['tenants', 'Empresas'], ['invoices', 'Faturas'], ['payments', 'Pagamentos'], ['email', 'E-mail'], ['nfse', 'NFS-e'], ['contact', 'WhatsApp'], ['google', 'Google'], ['releases', 'Atualizações'], ['account', 'Minha conta'], ['plans', 'Planos'], ['diagnostics', 'Diagnóstico']];
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
    const views = { tenants: tenantsView, new: newTenantView, tenant: tenantView, invoices: invoicesView, payments: paymentsView, email: emailView, nfse: nfseView, contact: contactView, google: googleView, releases: releasesView, account: accountView, plans: plansView, diagnostics: diagnosticsView };
    const content = await views[state.view]();
    main.replaceChildren(...[].concat(content));
    linkLabels(main);
    // O banner da empresa é atualizado após carregar o detalhe (sempre visível durante a manutenção).
    if (state.view === 'tenant' && !root.querySelector('.tenant-banner') && state.tenant)
      root.insertBefore(h('div', { class: 'tenant-banner' }, 'Empresa selecionada:', h('strong', {}, state.tenant.tradeName), h('span', {}, `(${state.tenant.code})`)), main);
  } catch (e) { if (e.message !== 'unauthorized') main.replaceChildren(h('div', { class: 'card error' }, e.message)); }
}

fetch('/config.json').then(r => r.json()).then(c => { state.api = c.apiBaseUrl.replace(/\/$/, ''); render(); });
