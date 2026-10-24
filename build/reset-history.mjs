#!/usr/bin/env node
// 把远程历史压成单个干净的初始提交（内部的开发过程不必对外）
//
// 用法：
//   GH_TOKEN=xxx node build/reset-history.mjs --message "初始发布" --dry-run
//   GH_TOKEN=xxx node build/reset-history.mjs --message "初始发布"
//
// 做的事：
//   1. 用当前 HEAD 的树建一个没有 parent 的提交
//   2. 把 main 指向它（force）
// 远程历史清空，内容不变。本地历史不受影响。

import { execFileSync } from "node:child_process";

const DRY_RUN = process.argv.includes("--dry-run");
const messageIndex = process.argv.indexOf("--message");
const MESSAGE = messageIndex >= 0 ? process.argv[messageIndex + 1] : null;
const TOKEN = process.env.GH_TOKEN;

if (!TOKEN) {
  console.error("缺少 GH_TOKEN");
  process.exit(1);
}

if (!MESSAGE) {
  console.error('缺少 --message "..."');
  process.exit(1);
}

const origin = execFileSync("git", ["remote", "get-url", "origin"], { encoding: "utf8" }).trim();
const match = origin.match(/github\.com[/:]([^/]+)\/([^/.]+?)(?:\.git)?$/);

if (!match) {
  console.error(`看不懂 origin：${origin}`);
  process.exit(1);
}

const [, OWNER, REPO] = match;
const BRANCH = "main";

const api = (path, init = {}) =>
  fetch(`https://api.github.com${path}`, {
    ...init,
    headers: {
      Authorization: `Bearer ${TOKEN}`,
      Accept: "application/vnd.github+json",
      "Content-Type": "application/json",
      ...(init.headers ?? {})
    }
  });

// 先按当前 HEAD 重建整棵树（保证内容是最新的，不依赖远程当时的样子）
const localFiles = [];

for (const line of execFileSync("git", ["-c", "core.quotepath=false", "ls-tree", "-r", "HEAD"], {
  encoding: "utf8"
}).split("\n")) {
  const hit = line.match(/^\d+ blob ([0-9a-f]+)\t(.+)$/);

  if (hit) {
    localFiles.push({ path: hit[2], sha: hit[1] });
  }
}

console.log(`本地 HEAD：${localFiles.length} 个文件`);

const refResponse = await api(`/repos/${OWNER}/${REPO}/git/ref/heads/${BRANCH}`);
const oldHead = refResponse.ok ? (await refResponse.json()).object.sha : null;
console.log(`远程 main 当前：${oldHead ? oldHead.slice(0, 8) : '(空)'}`);

if (DRY_RUN) {
  console.log(`会把这些提交压成 1 个：${MESSAGE}`);
  console.log("（dry-run，没有写入）");
  process.exit(0);
}

// 全量重建 blob（内容可能和远程不同，直接算一遍最稳）
const tree = [];

for (let i = 0; i < localFiles.length; i += 8) {
  await Promise.all(localFiles.slice(i, i + 8).map(async ({ path, sha }) => {
    const content = execFileSync("git", ["cat-file", "blob", sha], { maxBuffer: 64 * 1024 * 1024 });
    const response = await api(`/repos/${OWNER}/${REPO}/git/blobs`, {
      method: "POST",
      body: JSON.stringify({ content: content.toString("base64"), encoding: "base64" })
    });

    if (!response.ok) {
      throw new Error(`blob ${path} 失败：${response.status}`);
    }

    tree.push({ path, mode: "100644", type: "blob", sha: (await response.json()).sha });
  }));

  console.log(`  已传 ${Math.min(i + 8, localFiles.length)}/${localFiles.length}`);
}

const treeResponse = await api(`/repos/${OWNER}/${REPO}/git/trees`, {
  method: "POST",
  body: JSON.stringify({ tree })
});

if (!treeResponse.ok) {
  console.error("建 tree 失败：", (await treeResponse.text()).slice(0, 200));
  process.exit(1);
}

// parent 留空 = 孤儿提交，历史从这里重新开始
const commitResponse = await api(`/repos/${OWNER}/${REPO}/git/commits`, {
  method: "POST",
  body: JSON.stringify({
    message: MESSAGE,
    tree: (await treeResponse.json()).sha,
    parents: []
  })
});

if (!commitResponse.ok) {
  console.error("建 commit 失败：", (await commitResponse.text()).slice(0, 200));
  process.exit(1);
}

const commit = await commitResponse.json();

const refUpdate = await api(`/repos/${OWNER}/${REPO}/git/refs/heads/${BRANCH}`, {
  method: "PATCH",
  body: JSON.stringify({ sha: commit.sha, force: true })
});

if (!refUpdate.ok) {
  console.error("更新 ref 失败：", (await refUpdate.text()).slice(0, 200));
  process.exit(1);
}

console.log(`历史已重置：${commit.sha.slice(0, 8)}`);
console.log(`https://github.com/${OWNER}/${REPO}`);
