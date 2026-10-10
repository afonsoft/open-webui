// Testes de usabilidade + smoke visual: roda nas mesmas telas/viewports do
// axe (audit.js). Falha o job em: erro de console/pageerror, página em
// branco, overflow horizontal no mobile e foco de teclado inalcançável.
// Tap targets < 24x24px (WCAG 2.5.8) viram warnings com contagem.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const BASE = process.env.AUDIT_BASE_URL || 'http://127.0.0.1:5099';
const EMAIL = process.env.AUDIT_ADMIN_EMAIL || 'admin@ci.local';
const PASSWORD = process.env.AUDIT_ADMIN_PASSWORD || 'ci-admin-password-123';

const PAGES = [
  { name: 'chat', path: '/' },
  { name: 'workspace', path: '/workspace' },
  { name: 'admin', path: '/admin' },
];
const VIEWPORTS = [
  { name: 'mobile', width: 375, height: 812 },
  { name: 'desktop', width: 1280, height: 800 },
];
const SHOTS = path.join(__dirname, 'screenshots', 'usability');

const failures = [];
const warnings = [];
const consoleErrors = [];

async function checkPage(page, name, vp) {
  const ctx = `${name}@${vp.name}`;

  // 1) Conteúdo real renderizado (anti página-branca)
  const mainInfo = await page.evaluate(() => {
    const el = document.querySelector('#main-content') || document.body;
    const r = el.getBoundingClientRect();
    const visibles = [...el.querySelectorAll('*')]
      .filter(n => {
        const b = n.getBoundingClientRect();
        return b.width > 2 && b.height > 2;
      }).length;
    return { w: r.width, h: r.height, visibles, scrollW: document.documentElement.scrollWidth, innerW: window.innerWidth };
  });
  if (mainInfo.visibles < 10 || mainInfo.h < 50) {
    failures.push(`${ctx}: página quase em branco (${mainInfo.visibles} nós visíveis, h=${mainInfo.h})`);
  }
  // 2) Sem scroll horizontal (mobile principalmente)
  if (mainInfo.scrollW > mainInfo.innerW + 4) {
    failures.push(`${ctx}: overflow horizontal — scrollWidth ${mainInfo.scrollW} > innerWidth ${mainInfo.innerW}`);
  }

  // 3) Tap targets — WCAG 2.5.8 (24×24 mínimo)
  const small = await page.evaluate(() => {
    const sel = 'button, a[href], input, select, textarea, [role="button"], [tabindex="0"]';
    const tiny = [];
    for (const el of document.querySelectorAll(sel)) {
      const r = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      if (style.visibility === 'hidden' || style.display === 'none' || r.width === 0 || r.height === 0) continue;
      if (r.width < 24 || r.height < 24) {
        tiny.push(`${el.tagName.toLowerCase()} ${Math.round(r.width)}x${Math.round(r.height)} "${(el.textContent || el.getAttribute('aria-label') || '').trim().slice(0, 30)}"`);
      }
    }
    return tiny;
  });
  if (small.length > 0) {
    warnings.push(`${ctx}: ${small.length} tap target(s) < 24px — ${small.slice(0, 5).join('; ')}`);
  }

  // 4) Teclado: Tab deve alcançar um elemento interativo com foco visível
  await page.locator('body').click({ position: { x: 5, y: 5 }, timeout: 5000 }).catch(() => {});
  let reached = false;
  for (let i = 0; i < 15 && !reached; i++) {
    await page.keyboard.press('Tab');
    reached = await page.evaluate(() => {
      const el = document.activeElement;
      if (!el || el === document.body) return false;
      const t = el.tagName;
      if (['A', 'BUTTON', 'INPUT', 'SELECT', 'TEXTAREA'].includes(t)) return true;
      return el.getAttribute('tabindex') !== null && el.getAttribute('tabindex') !== '-1';
    });
  }
  if (!reached) {
    failures.push(`${ctx}: Tab não alcançou nenhum elemento interativo em 15 pressionamentos`);
  }

  // 5) Screenshot (evidência visual p/ diff manual e artefato do CI)
  const shot = path.join(SHOTS, `${name}-${vp.name}.png`);
  await page.screenshot({ path: shot, fullPage: false });
  const size = fs.statSync(shot).size;
  if (size < 25_000) {
    warnings.push(`${ctx}: screenshot suspeitamente pequeno (${size}B) — possível tela em branco`);
  }
}

(async () => {
  fs.mkdirSync(SHOTS, { recursive: true });
  const browser = await chromium.launch();

  for (const vp of VIEWPORTS) {
    const context = await browser.newContext({ viewport: { width: vp.width, height: vp.height } });
    const page = await context.newPage();
    page.on('console', m => {
      if (m.type() === 'error') consoleErrors.push(`${vp.name}: ${m.text().slice(0, 200)}`);
    });
    page.on('pageerror', e => consoleErrors.push(`${vp.name} pageerror: ${String(e).slice(0, 200)}`));

    // Auth (mesmo fluxo do audit.js — labels sem `for`, pill único no card)
    await page.goto(BASE + '/auth', { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('input[type="email"]', { timeout: 60000 });
    await page.waitForTimeout(1000);
    await checkPage(page, 'auth', vp);

    await page.locator('input[type="email"]').first().fill(EMAIL);
    await page.locator('input[type="password"]').first().fill(PASSWORD);
    await page.locator('input[type="password"]').first().press('Tab');
    await page.locator('button.w-full.rounded-full').first().click();
    await page.waitForFunction(() => !location.pathname.includes('auth'), null, { timeout: 30000 });
    await page.waitForSelector('#main-content', { timeout: 60000 });
    await page.waitForLoadState('networkidle');

    for (const p of PAGES) {
      await page.goto(BASE + p.path, { waitUntil: 'domcontentloaded' });
      await page.waitForSelector('#main-content', { timeout: 60000 });
      await page.waitForTimeout(1500);
      await checkPage(page, p.name, vp);
    }
    await context.close();
  }

  await browser.close();

  const report = { failures, warnings, consoleErrors };
  fs.writeFileSync('usability-report.json', JSON.stringify(report, null, 2));
  for (const w of warnings) console.log(`[warn] ${w}`);
  for (const e of consoleErrors.slice(0, 10)) console.log(`[console.error] ${e}`);
  console.log(`usability: ${failures.length} falhas, ${warnings.length} warnings, ${consoleErrors.length} erros de console`);
  if (failures.length || consoleErrors.length) {
    for (const f of failures) console.error(`::error::${f}`);
    if (consoleErrors.length) console.error(`::error::${consoleErrors.length} erro(s) de console no browser`);
    process.exit(1);
  }
})();
