import { defineConfig } from "vitest/config";
import { sveltekit } from "@sveltejs/kit/vite";
import { svelteTesting } from "@testing-library/svelte/vite";

// Unit and component tests. Tauri's invoke is mocked per test; nothing reaches the Rust side.
export default defineConfig({
  plugins: [sveltekit(), svelteTesting()],
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.ts"],
  },
});
