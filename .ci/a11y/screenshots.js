// Captura screenshots das telas para o README (docs/screenshots/).
// Roda contra o app publicado: AUDIT_BASE_URL + ADMIN_* seeds.
const { chromium } = require('playwright');
const fs = require('fs');

const BASE = process.env.AUDIT_BASE_URL || 'http://127.0.0.1:5099';
const EMAIL = process.env.AUDIT_ADMIN_EMAIL || 'admin@ci.local';
const PASSWORD = process.env.AUDIT_ADMIN_PASSWORD || 'ci-admin-password-123';
const OUT = process.env.SHOT_OUT || '../../docs/screenshots';

const SHOTS = [
  // [arquivo, path, viewport, theme, ação extra]
  ['auth-dark', '/auth', { width: 1280, height: 800 }, 'dark'],
  ['chat-dark', '/', { width: 1280, height: 800 }, 'dark'],
  ['chat-light', '/', { width: 1280, height: 800 }, 'light'],
  ['workspace-dark', '/workspace', { width: 1280, height: 800 }, 'dark'],
  ['admin-dark', '/admin', { width: 1280, height: 800 }, 'dark'],
  ['chat-mobile', '/', { width: 375, height: 720 }, 'dark', 'mobile-menu'],
  ['admin-mobile', '/admin', { width: 375, height: 720 }, 'light'],
];

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();

  // login
  await page.goto(BASE + '/auth', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('input[type="email"]', { timeout: 60000 });
  await page.locator('input[type="email"]').first().fill(EMAIL);
  await page.locator('input[type="password"]').first().fill(PASSWORD);
  await page.locator('input[type="password"]').first().press('Tab');
  await page.locator('button.w-full.rounded-full').first().click();
  await page.waitForFunction(() => !location.pathname.includes('auth'), null, { timeout: 30000 });
  await page.waitForSelector('#main-content', { timeout: 60000 });
  await page.waitForLoadState('networkidle');

  for (const [name, path, vp, theme, extra] of SHOTS) {
    await page.setViewportSize(vp);
    // tema via localStorage + reload do shell
    await page.evaluate(t => localStorage.setItem('webui.theme', t), theme);
    await page.goto(BASE + path, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector(path === '/auth' ? 'input[type="email"]' : '#main-content', { timeout: 60000 });
    if (extra === 'mobile-menu') {
      // abre a gaveta mobile para mostrar o drawer
      const btn = page.locator('button[aria-label*="menu" i], button[aria-label*="sidebar" i]').first();
      if (await btn.count()) await btn.click().catch(() => {});
    }
    await page.waitForTimeout(1800); // settle/hidratação/anim
    await page.screenshot({ path: `${OUT}/${name}.png` });
    console.log('shot:', name);
  }
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
