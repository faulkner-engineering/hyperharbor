import { defineConfig } from "@playwright/test";

// Browser tests of the built tray UI (npm run test:e2e). vitest and jsdom cannot see the cascade the way WebView2 does:
// a global style in one component that turned the whole window black passed every unit test. These tests load the real
// build in Chromium, give it a state through a stand-in for WebView2's message channel, and check what is drawn.
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: true,
  reporter: [["list"]],
  use: { baseURL: "http://127.0.0.1:4173", viewport: { width: 980, height: 660 } },
  webServer: {
    command: "npm run build && npx vite preview --host 127.0.0.1 --port 4173 --strictPort",
    url: "http://127.0.0.1:4173",
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
