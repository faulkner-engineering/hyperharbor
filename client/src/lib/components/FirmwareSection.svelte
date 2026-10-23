<script lang="ts">
  import type { MachineDraft } from "$lib/machineDraft.svelte";
  import Choice from "./Choice.svelte";

  interface Props {
    draft: MachineDraft;
  }

  let { draft }: Props = $props();

  const OPTIONS = [
    { value: null, label: "As it is" },
    { value: true, label: "On" },
    { value: false, label: "Off" },
  ];
</script>

<Choice
  label="Virtual TPM"
  options={OPTIONS}
  bind:value={draft.firmware.tpm}
  hint="Windows 11 needs one unless Setup's hardware checks are bypassed. Applying a profile can add a TPM to a VM, never remove one."
/>
<Choice
  label="Secure Boot"
  options={OPTIONS}
  bind:value={draft.firmware.secureBoot}
  hint="The certificate template follows the OS: Microsoft Windows, or the UEFI certificate authority for Linux."
/>
