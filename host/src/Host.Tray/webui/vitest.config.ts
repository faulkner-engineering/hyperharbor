import { defineConfig } from "vitest/config";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import { svelteTesting } from "@testing-library/svelte/vite";

// Component tests run against a fake bridge; nothing reaches the tray.
export default defineConfig({
  plugins: [svelte(), svelteTesting()],
  // The shared components (@hyperharbor/ui, a link to ui/ at the repository root) import svelte from ui/node_modules;
  // one copy of the runtime must serve both, or their reactivity would not connect.
  resolve: {
    dedupe: ["svelte"],
  },
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.ts"],
    setupFiles: ["src/test-setup.ts"],
  },
});
