// Opens the published smoke app in headless Chromium and fails unless every check on the page passed.
// Usage: node run.mjs <url>
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const url = process.argv[2];
const browser = await chromium.launch();
try {
  const page = await browser.newPage();
  const errors = [];
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', e => errors.push(String(e)));
  await page.goto(url);
  await page.waitForFunction(() => /DONE/.test(document.querySelector('#result')?.textContent ?? ''), null, { timeout: 120000 });
  const result = await page.textContent('#result');
  console.log(result);
  if (errors.length) console.log('Browser console errors:\n' + errors.join('\n'));
  if (/FAIL/.test(result) || errors.length) process.exitCode = 1;
} finally {
  await browser.close();
}
