#!/usr/bin/env node
// 用 GitHub API 同步到远程仓库（github.com 直连不通时用这个代替 git push）
//
// 用法：
//   GH_TOKEN=ghp_xxx node build/sync-github.mjs
//   GH_TOKEN=ghp_xxx node build/sync-github.mjs --dry-run    只列出会变什么，不写
//
// 行为：
//   - 和 git push 同语义：同步的是 HEAD（未提交的工作区改动不会上传）
//   - 增量：只上传 blob sha 变了的文件，没变的沿用远程的
//   - 本地删掉的文件，远程也会删
//
// 注意：这台机器直连 github.com 会超时，所以 git push 走不通，才需要这条路径。
// 网络恢复后建议还是用普通 git push，历史会更好看。

import { execFileSync } from "node:child_process";

const DRY_RUN = process.argv.includes("--dry-run");

// --message "..." 可覆盖提交信息（默认用 HEAD 的标题）
const messageIndex = process.argv.indexOf("--message");
const MESSAGE = messageIndex >= 0 ? process.argv[messageIndex + 1] : null;
const TOKEN = process.env.GH_TOKEN;

if (!TOKEN) {
  console.error("缺少 GH_TOKEN 环境变量");
  process.exit(1);
}

// ── 从 origin 解析 owner / repo ──────────────────────
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

// ── 本地 HEAD 的文件清单（path → blob sha）──────────
const localFiles = new Map();

for (const line of execFileSync("git", ["-c", "core.quotepath=false", "ls-tree", "-r", "HEAD"], {
  encoding: "utf8"
}).split("\n")) {
  if (!line.trim()) {
    continue;
  }

  // 100644 blob <sha>\t<path>
  const [, sha, path] = line.match(/^\d+ blob ([0-9a-f]+)\t(.+)$/) ?? [];

  if (sha && path) {
    localFiles.set(path, sha);
  }
}

console.log(`本地 HEAD：${localFiles.size} 个文件`);

// ── 远程当前状态 ────────────────────────────────────
const refResponse = await api(`/repos/${OWNER}/${REPO}/git/ref/heads/${BRANCH}`);

if (!refResponse.ok) {
  console.error(`拿不到远程分支 ${BRANCH}：${refResponse.status}`);
  process.exit(1);
}

const remoteHead = (await refResponse.json()).object.sha;
const remoteCommit = await (await api(`/repos/${OWNER}/${REPO}/git/commits/${remoteHead}`)).json();
const remoteTreeSha = remoteCommit.tree.sha;

const remoteTree = await (await api(`/repos/${OWNER}/${REPO}/git/trees/${remoteTreeSha}?recursive=1`)).json();
const remoteFiles = new Map(
  (remoteTree.tree ?? [])
    .filter((item) => item.type === "blob")
    .map((item) => [item.path, item.sha]));

console.log(`远程 main：${remoteFiles.size} 个文件 @ ${remoteHead.slice(0, 8)}`);

// ── 算差异 ──────────────────────────────────────────
const added = [];
const modified = [];
const removed = [];

for (const [path, sha] of localFiles) {
  const remoteSha = remoteFiles.get(path);

  if (!remoteSha) {
    added.push(path);
  } else if (remoteSha !== sha) {
    modified.push(path);
  }
}

for (const path of remoteFiles.keys()) {
  if (!localFiles.has(path)) {
    removed.push(path);
  }
}

console.log(`新增 ${added.length} · 修改 ${modified.length} · 删除 ${removed.length}`);

if (added.length === 0 && modified.length === 0 && removed.length === 0) {
  console.log("已经同步，无需操作");
  process.exit(0);
}

if (DRY_RUN) {
  [...added].forEach((path) => console.log(`  + ${path}`));
  [...modified].forEach((path) => console.log(`  ~ ${path}`));
  [...removed].forEach((path) => console.log(`  - ${path}`));
  console.log("（dry-run，没有写入）");
  process.exit(0);
}

// ── 上传变化的 blob ─────────────────────────────────
const tree = [];

async function addBlob(path) {
  // 直接问 git 要内容，避免受工作区未提交改动影响
  const content = execFileSync("git", ["cat-file", "blob", localFiles.get(path)], {
    maxBuffer: 64 * 1024 * 1024
  });

  const response = await api(`/repos/${OWNER}/${REPO}/git/blobs`, {
    method: "POST",
    body: JSON.stringify({ content: content.toString("base64"), encoding: "base64" })
  });

  if (!response.ok) {
    throw new Error(`blob ${path} 失败：${response.status} ${(await response.text()).slice(0, 160)}`);
  }

  tree.push({ path, mode: "100644", type: "blob", sha: (await response.json()).sha });
}

const changed = [...added, ...modified];

for (let i = 0; i < changed.length; i += 8) {
  await Promise.all(changed.slice(i, i + 8).map(addBlob));
  console.log(`  已传 ${Math.min(i + 8, changed.length)}/${changed.length}`);
}

for (const path of removed) {
  tree.push({ path, mode: "100644", type: "blob", sha: null });
}

// ── tree → commit → ref ─────────────────────────────
const treeResponse = await api(`/repos/${OWNER}/${REPO}/git/trees`, {
  method: "POST",
  body: JSON.stringify({ base_tree: remoteTreeSha, tree })
});

if (!treeResponse.ok) {
  console.error("建 tree 失败：", (await treeResponse.text()).slice(0, 300));
  process.exit(1);
}

const subject = MESSAGE ?? execFileSync("git", ["log", "-1", "--pretty=%s"], { encoding: "utf8" }).trim();
const body = MESSAGE ? "" : execFileSync("git", ["log", "-1", "--pretty=%b"], { encoding: "utf8" }).trim();
const author = execFileSync("git", ["log", "-1", "--pretty=%an <%ae>"], { encoding: "utf8" }).trim();

const commitResponse = await api(`/repos/${OWNER}/${REPO}/git/commits`, {
  method: "POST",
  body: JSON.stringify({
    message: body ? `${subject}\n\n${body}` : subject,
    tree: (await treeResponse.json()).sha,
    parents: [remoteHead],
    author: parseAuthor(author)
  })
});

if (!commitResponse.ok) {
  console.error("建 commit 失败：", (await commitResponse.text()).slice(0, 300));
  process.exit(1);
}

const commit = await commitResponse.json();

const refUpdate = await api(`/repos/${OWNER}/${REPO}/git/refs/heads/${BRANCH}`, {
  method: "PATCH",
  body: JSON.stringify({ sha: commit.sha, force: false })
});

if (!refUpdate.ok) {
  console.error("更新 ref 失败：", (await refUpdate.text()).slice(0, 300));
  process.exit(1);
}

console.log(`已同步：${commit.sha.slice(0, 8)}（+${added.length} ~${modified.length} -${removed.length}）`);
console.log(`https://github.com/${OWNER}/${REPO}/commit/${commit.sha}`);

/// 从 "Name <mail>" 拆出 GitHub 要的 name / email
function parseAuthor(value) {
  const hit = value.match(/^(.*) <(.*)>$/);

  return hit ? { name: hit[1], email: hit[2] } : undefined;
}
