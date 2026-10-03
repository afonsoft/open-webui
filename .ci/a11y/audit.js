// Auditoria de acessibilidade: roda axe-core nas telas principais em
// viewport mobile (375px) e desktop. Falha o job em violações "critical";
// "serious" viram warnings no relatório até o baseline zerar.
const { chromium } = require('playwright');
const { AxeBuilder } = require('@axe-core/playwright');
const fs = require('fs');

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

async function analyze(page, context) {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();
  return results.violations.map(v => ({
    context,
    id: v.id,
    impact: v.impact,
    help: v.help,
    nodes: v.nodes.slice(0, 5).map(n => n.target.join(' ')),
    nodeCount: v.nodes.length,
  }));
}

(async () => {
  const browser = await chromium.launch();
  const all = [];

  for (const vp of VIEWPORTS) {
    // @axe-core/playwright exige página criada via browser.newContext().
    const context = await browser.newContext({ viewport: { width: vp.width, height: vp.height } });
    const page = await context.newPage();
    const ctx = name => `${name}@${vp.name}`;

    // Tela de auth (sem sessão).
    await page.goto(BASE, { waitUntil: 'networkidle' });
    all.push(...await analyze(page, ctx('auth')));

    // Login real via formulário (admin semeado por ADMIN_EMAIL/ADMIN_PASSWORD).
    // Os <label> da tela de auth não têm `for`/id — seleção por type.
    await page.locator('input[type="email"]').first().fill(EMAIL);
    await page.locator('input[type="password"]').first().fill(PASSWORD);
    await page.locator('input[type="password"]').first().press('Enter');
    await page.waitForURL(u => !u.pathname.includes('auth'), { timeout: 30000 });
    await page.waitForLoadState('networkidle');

    for (const p of PAGES) {
      await page.goto(BASE + p.path, { waitUntil: 'networkidle' });
      await page.waitForTimeout(1500); // WASM hydration
      all.push(...await analyze(page, ctx(p.name)));
    }
    await context.close();
  }

  await browser.close();

  fs.writeFileSync('a11y-report.json', JSON.stringify(all, null, 2));

  const critical = all.filter(v => v.impact === 'critical');
  const serious = all.filter(v => v.impact === 'serious');
  const minor = all.length - critical.length - serious.length;
  console.log(`axe: ${all.length} violações — ${critical.length} critical, ${serious.length} serious, ${minor} minor/moderate`);
  for (const v of critical.concat(serious)) {
    console.log(`[${v.impact}] ${v.context} ${v.id}: ${v.help} (${v.nodeCount} nós) ex: ${v.nodes[0] || '?'}`);
  }
  if (critical.length > 0) {
    console.error(`::error::${critical.length} violação(ões) critical de acessibilidade`);
    process.exit(1);
  }
})();
