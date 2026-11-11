import { mount } from "svelte";
// The shared components' tokens and control styles first, then the tray's own theme.
import "@hyperharbor/ui/theme.css";
import "./theme.css";
import App from "./App.svelte";
import { provideTrayHostApi } from "./hostApi";

provideTrayHostApi();

const app = mount(App, { target: document.getElementById("app")! });

export default app;
