import { defineConfig } from "vitest/config";
import { sveltekit } from "@sveltejs/kit/vite";
import { vitePreprocess } from "@sveltejs/vite-plugin-svelte";
import { svelteTesting } from "@testing-library/svelte/vite";

// Unit and component tests. Tauri's invoke is mocked per test; nothing reaches the Rust side.
// SvelteKit 3 takes its options through the plugin (vite.config.js); tests need the preprocessor but not the adapter.
export default defineConfig({
  plugins: [sveltekit({ preprocess: vitePreprocess() }), svelteTesting()],
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.ts"],
    setupFiles: ["src/test-setup.ts"],
  },
});
