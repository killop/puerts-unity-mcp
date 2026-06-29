# 在 C# 中调用 Lua

> 💡 PuerTS 3.0 同时支持 C# 调用 [Javascript](./cs2js.md) 和 [Python](./cs2python.md)，语法各有不同，可点击链接查看对应教程。

### 通过 Delegate 调用

PuerTS 提供了一个关键能力：将 Lua 函数转换为 C# 的 delegate。依靠这个能力，你就可以在 C# 侧调用 Lua 函数。

```csharp
public delegate void TestCallback(string msg);

public class TestClass
{
    public TestCallback Callback;

    public void TriggerCallback()
    {
        if (Callback != null)
        {
            Callback("hello_from_csharp");
        }
    }
}

void Start()
{
    var env = new Puerts.ScriptEnv(new Puerts.BackendLua());
    env.Eval(@"
        local CS = require('csharp')
        -- Create a C# object
        local obj = CS.TestClass()
        -- Assign a Lua function to the C# delegate property
        obj.Callback = function(msg)
            info = msg
        end
        -- Trigger the callback from C# side
        obj:TriggerCallback()
    ");
    // info is now 'hello_from_csharp'
    env.Dispose();
}
```

> ⚠️ 注意：在 Lua 中给 C# 对象的 delegate 属性赋值时，使用**点号**语法 `obj.Callback = function(...) end`。调用实例方法时使用**冒号**语法 `obj:TriggerCallback()`。

你也可以在 Lua 侧主动调用 delegate 的 `Invoke` 方法：

```lua
-- Directly invoke the delegate from Lua
obj.Callback:Invoke('hello_from_lua')
```

------------------

### 从 C# 往 Lua 传参

把 Lua 函数转换成 delegate 时，可以将其转换成带参数的 delegate，这样就可以把 C# 变量传递给 Lua。传参时，类型转换的规则和把变量从 C# 返回到 Lua 是一致的。

```csharp
void Start()
{
    var env = new Puerts.ScriptEnv(new Puerts.BackendLua());
    // Get a Lua function as a C# delegate via Eval
    System.Action<int> LogInt = env.Eval<System.Action<int>>(@"
        return function(a)
            print(a)
        end
    ");

    LogInt(3); // Output: 3
    env.Dispose();
}
```

> ⚠️ **重要差异**：与 Javascript 不同，Lua 的 `Eval` 返回值需要使用 **`return`** 语句显式返回。如果你忘记写 `return`，C# 侧将得到 `null`。

> 需要注意的是，如果你生成的 delegate 带有值类型参数，需要添加 UsingAction 或者 UsingFunc 声明。具体请参见 FAQ

------------------

### 从 C# 调用 Lua 并获得返回值

与上一部分类似，只需要将 Action delegate 变成 Func delegate 就可以了。

```csharp
void Start()
{
    var env = new Puerts.ScriptEnv(new Puerts.BackendLua());
    // Get a Lua function that returns a value
    System.Func<int, int> Add3 = env.Eval<System.Func<int, int>>(@"
        return function(a)
            return 3 + a
        end
    ");

    System.Console.WriteLine(Add3(1)); // Output: 4
    env.Dispose();
}
```

你也可以直接使用 `Eval<T>` 来获取简单的返回值：

```csharp
void Start()
{
    var env = new Puerts.ScriptEnv(new Puerts.BackendLua());
    // Directly evaluate and get the return value
    int result = env.Eval<int>("return 1 + 2");
    System.Console.WriteLine(result); // Output: 3

    string str = env.Eval<string>("return 'hello lua'");
    System.Console.WriteLine(str); // Output: hello lua
    env.Dispose();
}
```

> ⚠️ 再次提醒：Lua 中必须使用 `return` 语句来返回值，这是与 Javascript 最大的区别之一。在 JS 中，表达式的最后一个值会被自动返回，而 Lua 中不写 `return` 则不会有返回值。

> 需要注意的是，如果你生成的 delegate 带有值类型参数，需要添加 UsingAction 或者 UsingFunc 声明。具体请参见 FAQ

------------------

### Lua 中的错误处理

当 Lua 代码中使用 `error()` 抛出异常时，C# 侧可以通过 `try-catch` 捕获：

```csharp
void Start()
{
    var env = new Puerts.ScriptEnv(new Puerts.BackendLua());

    // Lua error will be caught as a C# exception
    try
    {
        env.Eval("error('something went wrong')");
    }
    catch (Exception e)
    {
        Debug.Log(e.Message); // Contains: something went wrong
    }

    // Errors in Lua functions converted to delegates are also catchable
    try
    {
        var foo = env.Eval<Action>(@"
            return function()
                error('error in function')
            end
        ");
        foo(); // This will throw
    }
    catch (Exception e)
    {
        Debug.Log(e.Message); // Contains: error in function
    }

    env.Dispose();
}
```

------------------

### 环境销毁与 Delegate 生命周期

当 Lua 环境（`ScriptEnv`）被 `Dispose()` 后，之前转换的 delegate 将不再可用。调用已销毁环境的 delegate 会抛出异常，请务必注意管理好生命周期。

```csharp
void Start()
{
    var env = new Puerts.ScriptEnv(new Puerts.BackendLua());
    System.Action<string> luaFunc = env.Eval<System.Action<string>>(@"
        return function(msg)
            print(msg)
        end
    ");

    luaFunc("before dispose"); // OK

    env.Dispose();

    // ❌ This will throw an exception!
    // luaFunc("after dispose");
}
```

------------------

### 在 Lua 中实现 MonoBehaviour

综合上面所有能力，我们可以在 Lua 里实现 MonoBehaviour 的生命周期回调：

```csharp
using System;
using Puerts;
using UnityEngine;

public class LuaBehaviour : MonoBehaviour
{
    public Action LuaStart;
    public Action LuaUpdate;
    public Action LuaOnDestroy;

    static ScriptEnv luaEnv;

    void Awake()
    {
        if (luaEnv == null) luaEnv = new ScriptEnv(new BackendLua());

        var init = luaEnv.Eval<Action<MonoBehaviour>>(@"
            return function(bindTo)
                -- Bind Lua functions to C# delegate properties
                bindTo.LuaUpdate = function()
                    print('update...')
                end
                bindTo.LuaOnDestroy = function()
                    print('onDestroy...')
                end
            end
        ");

        if (init != null) init(this);
    }

    void Start()
    {
        if (LuaStart != null) LuaStart();
    }

    void Update()
    {
        if (LuaUpdate != null) LuaUpdate();
    }

    void OnDestroy()
    {
        if (LuaOnDestroy != null) LuaOnDestroy();
        LuaStart = null;
        LuaUpdate = null;
        LuaOnDestroy = null;
    }
}
```

> ⚠️ 注意 Lua 与 JS 的关键差异：
> - Lua 的 `Eval` 必须使用 `return` 返回函数
> - Lua 中赋值 delegate 属性使用**点号**语法：`bindTo.LuaUpdate = function() ... end`
> - Lua 中调用 C# 实例方法使用**冒号**语法：`bindTo:SomeMethod()`

------------------

### Lua 与 Javascript 在 C# 调用方面的主要差异

| 特性 | Javascript | Lua |
|------|-----------|-----|
| Eval 返回值 | 表达式最后一个值自动返回 | 必须使用 `return` 显式返回 |
| 函数语法 | `(a) => { ... }` 或 `function(a) { ... }` | `function(a) ... end` |
| delegate 赋值 | `obj.Callback = (msg) => { ... }` | `obj.Callback = function(msg) ... end` |
| 方法调用 | 统一使用点号 `obj.Method()` | 实例方法使用冒号 `obj:Method()` |
| 输出到控制台 | `console.log()` | `print()` |
| 空值 | `null` / `undefined` | `nil` |

----------------

> 📖 其他语言的 C# 调用教程：[C# 调用 Javascript](./cs2js.md) | [C# 调用 Python](./cs2python.md) | [三语言对比速查表](./lang-comparison.md)
