<template><a-config-provider><router-view /></a-config-provider></template>
<style>
body { margin: 0; color: #1f2937; background: #f4f6fa; }
.page-heading { display: flex; align-items: center; justify-content: space-between; gap: 16px; margin-bottom: 24px; }
.page-heading h1 { margin: 0 0 6px; font-size: 26px; letter-spacing: -.5px; }
.page-heading p { color: #64748b; margin: 0; }
.toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; margin-bottom: 20px; }
.toolbar .ant-select { min-width: 150px; }
.toolbar .ant-input-search { width: 280px; }
.data-card { background: white; border: 1px solid #e8edf3; border-radius: 10px; padding: 24px; }
.muted { color: #64748b; font-size: 12px; }
.mono { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; overflow-wrap: anywhere; }
pre { padding: 16px; background: #f5f7fa; border-radius: 6px; white-space: pre-wrap; overflow-wrap: anywhere; max-height: 360px; overflow: auto; }
.split { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; }
@media (max-width: 700px) { .split { grid-template-columns: 1fr; } .data-card { padding: 14px; } .page-heading { align-items: flex-start; } }
</style>
