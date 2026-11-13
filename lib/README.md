# lib

编译用的引用程序集，只含类型与成员签名、不含任何方法体（由 [Refasmer](https://github.com/JetBrains/Refasmer) 从 BepInEx 6 be.793 生成的 interop 程序集剥离而来），仅供编译期引用，运行时不会也不能被加载。

游戏更新后如需重新生成：在装好 BepInEx 并启动过一次的游戏目录里取 `BepInEx/core` 与 `BepInEx/interop` 下的同名文件，执行

```sh
refasmer -c -n --omit-non-api-members=true -O lib <dll...>
refasmer -n --all -o lib/UnityEngine.CoreModule.dll <path>/UnityEngine.CoreModule.dll   # 这个文件用默认参数会失败
```
