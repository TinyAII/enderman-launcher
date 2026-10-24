# ADR 0002：插件契约照启动器长，不照搬 Cordis

- 状态：已定
- 日期：2026-10-21
- 相关：ADR 0001（技术栈）、`docs/01-插件系统设计.md`

---

## 1. 背景与触发

用户要求参照 DeepSeek Harness（DSH）的开源与插件化做法。实地核实后确认：

- DSH 仓库 `deepseek-ai/deepseek-harness`，**MIT**，描述为 `DeepSeek Harness: Everything is a Plugin.`，TypeScript，256,197 star / 30,882 fork（2026-10-21 查 GitHub API）。
- 其插件内核是 **Cordis**（`@deepseek-ai/cordis`）：`Context` 容器 + `Service` 抽象类 + `Fiber` 生命周期 + `inject` 依赖声明 + `cordis.patch.yml` 配置层装配。
- 106 个 DSH 包在自己的 `lib` 里注册 `ctx` 服务；连 Agent 主循环都只是名为 `agents` 的服务（`dsh-agent`），`dsh-session` → `sessions`，`dsh-sandbox` → `sandbox`，`dsh-compaction` → `compaction`。
- 但 `cordis/src/reflect.ts:290` 明确抛错 `service "${name}" has been registered at <fiber>`：**同名服务不许抢占**。所以「替换组件」不是抢名字，而是在配置层把原来那个 `disabled` 掉、在同一位置 `insert` 自己的实现。

**用户的纠正（原话）**：「你不要纯抄啊，他这是跑AI的，和我们根本不是一种东西。」

这条纠正成立，且下面的差异是结构性的，不是口味问题。

---

## 2. 为什么不能照搬：两类程序的硬差异

| 维度 | DSH（AI 代理宿主） | 我们（MC 启动器） |
|---|---|---|
| 生命周期 | 常驻进程，会话可能持续数小时，Agent 循环不停被驱动 | 一次性任务：解析 → 下载 → 校验 → 拼参数 → 起 JVM → 用户玩 → 进程退出 |
| 扩展对象 | **行为**（模型怎么想、工具怎么调、上下文怎么压） | **数据与端点**（从哪下载、用哪个加载器装、怎么登录、界面加一页） |
| 可替换深度 | 连主循环都可换（`agents` 只是服务名） | 启动管线**不许**换：那是内核，换了就不叫启动器了 |
| 插件规模 | 数百个，互相依赖成服务图，需要 DI 解决装配顺序 | 几十个，能力面窄，依赖基本是「有/无」而不是「谁的实现」 |
| 失败代价 | 一轮回答失败，可以重试，会话继续 | JVM 起了个错参数 → 用户看到崩溃窗口，不知道谁的责任 |
| 热插拔价值 | 高（改提示词/换模型不重启会话） | **低**（换个下载源，下次启动生效即可；运行中热换会制造「一半任务走旧源」的诡异状态） |
| 信任模型 | 插件作者与宿主同一方（官方生态） | 插件来自陌生人，且能碰账号令牌与用户硬盘 |

结论：DSH 需要**服务容器**，是因为它有一张会变的依赖图；我们需要的是**能力注册表**，因为我们的依赖图是固定的、只在少数几个点上开放。

---

## 3. 决策

### 3.1 采纳（4 条，都是机制而非风格）

1. **契约程序集独立发布、独立许可**。`ML.Plugin.Abstractions` 单独成包（独立仓库/独立 NuGet），以宽松许可发布，插件作者不必碰宿主源码即可编译。这是 DSH 生态能起量的第一因，与 MIT 同样重要。
2. **官方功能也写成插件**。已经在做：`plugins/*` 禁止引用 `ML.Core.*`，`ArchitectureTests` 断言。安装器（现 `ML.Features`）将是第一个。
3. **装配是数据不是代码**。一份声明式配置列出启用哪些插件及其配置；**不经重新编译就能换掉一个能力实现**。我们用自己的 `plugins.json`，不用 Cordis 的 YAML 补丁语法（`!!js` 那种在 C# 侧没有对应物，硬造只会得到一门半吊子 DSL）。
4. **插件管理与市场本身也是插件**。避免「管理插件的代码」长成特权组件。

### 3.2 拒绝（5 条，每条附理由）

| 拒绝项 | 理由 |
|---|---|
| DI 容器 / `inject` 依赖图 / 拓扑排序 | 我们只有 7 族能力、几十个插件，`Dictionary<string, List<ICapability>>` + 优先级就够。引入 DI 会新增一整层需要长期维护与版本化的 API，而它解决的问题我们没有。 |
| `Fiber` / effect 一律可撤销的水瀑布 | 启动器的主要资源是**子进程与文件句柄**，它们的释放点明确（JVM 退出、任务结束）。全链路 effect 追踪是给「长期运行、随时重载」的系统准备的，我们不需要，且它会渗进每个接口签名。 |
| 进程内插件热重载 | `AssemblyLoadContext` 卸载后仍有静态状态与计时器残留的老问题；启动器重启只要 0.5 秒，**重启比热重载可靠**。改为：装/卸/启停插件后提示「重启生效」。 |
| YAML 补丁树 + `!!js` 表达式 | 等于自造一门配置语言，需要自己的文档、错误信息、IDE 支持。我们给 JSON + 一个可校验的 schema。 |
| Volatile 配置（运行中改引用不重启） | 同上，收益集中在长驻会话型应用。 |

### 3.3 我们实际要建的机制（比 Cordis 小一个数量级）

```
ML.Plugin.Abstractions   ← 冻结契约
  ├─ IPluginManifest                    插件自述
  ├─ IP lugin                          生命周期：Initialize / Shutdown（无 effect 树）
  ├─ 能力接口（7 族，见 §4）
  └─ CapabilityRegistry                 name → 有序实现列表（内置 + 插件），按 priority 取用
        ↑
ML.Host  → 读 plugins.json → 加载 → 逐个注册能力 → 各业务点向注册表要实现
```

关键约定：

- **能力是「有多个实现，按需取一个或全部」**，不是「谁替换谁」。例如 `IDownloadMirror` 可以有多个，按优先级与可用性挑；`IInstaller` 可以有多个，按版本匹配挑。
- **同名服务冲突不报错，改为优先级排序**——因为启动器的场景是「多来源」，不是「唯一实现」。这一点与 Cordis 相反，且是我们的场景决定的，不是懒。
- **插件不可替换内核步骤**。内核 21 个文件（解析/合并/rules/classpath/参数/进程）不暴露扩展点。想改启动行为，只能通过**参数片段**（Tier1 声明式）与**已定义的钩子**（开火前后、退出归因），不能替换步骤本身。

---

## 4. 启动器真正开放的 7 族能力

这是本 ADR 最该被后来者读的一段：插件到底能碰什么。

| 族 | 接口 | 典型实现 | 档位 |
|---|---|---|---|
| 安装源 | `IInstallSource` | 官方源、BMCLAPI 镜像、自建对象存储、整合包托管站 | 1/2/3 |
| 下载镜像 | `IDownloadMirror` | 把 Mojang/Forge/Fabric 的 URL 重写到镜像 | 1 |
| 加载器安装 | `ILoaderInstaller` | Forge / NeoForge / Fabric / Quilt / LiteLoader | 2/3 |
| 账号 | `IAccountProvider` | 微软 OAuth、离线、authlib-injector（第三方皮肤站） | 2/3 |
| 界面 | `IUiContribution` | 新增页面、设置页、详情面板（沙箱 iframe 或声明式树） | 1/2 |
| 钩子 | `ILaunchHook` | 启动前改文件、装 JVM 参数、退出后收集崩溃报告 | 2/3 |
| 集成包导入 | `IModpackImporter` | CurseForge / Modrinth / HMCL / MultiMC 格式 | 2/3 |

不在表里的，就是内核或者未来要新增的族——**新增一族意味着冻结一个接口，代价是永久的**，所以宁缺勿滥。

---

## 5. 后果

- `docs/01-插件系统设计.md` 的三档形态、manifest schema、权限模型、UI 双轨制**继续有效**（它本来就是按启动器写的）；需要补的是本 ADR 的 7 族能力表与「不做 DI/热重载」的边界。
- `ML.Plugin.Abstractions` 的内容因此比原计划**更小**：不做容器，只有 manifest + 能力接口 + 注册表。
- 需要新增 `CapabilityRegistry` 与其单测；`ShellHost` 的下载/安装相关分支改为「向注册表要 `IInstallSource`，没有就如实报告没有」——这与已实现的「内核如实拒绝安装」一脉相承。
- 「一切皆插件」这句话对我们成立的范围是：**7 族能力 + 官方功能**，不是「连内核步骤都能换」。对外表述必须按这个说法，否则会招来「为什么我不能换掉 classpath 构造」的 issue。

---

## 6. 待办

1. `CapabilityRegistry`（约 150 行 + 单测）：`Register(name, impl, priority, source)`、`Get<T>(name)`、`GetAll<T>(name)`、`Describe()` 供界面展示「这个能力当前由谁提供」。
2. `plugins.json` schema 与加载器；加载失败隔离（一个插件起不来不影响其他）。
3. 把 `ML.Features` 的下载引擎改造成注册表里的第一个 `IInstallSource` / `IDownloadMirror` 实现，验证契约够用。
4. `docs/01` 补 7 族能力表与「拒绝项」清单。
