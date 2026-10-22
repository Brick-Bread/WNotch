using Notch.Core.Widgets;

namespace Notch.Core.Tests;

public class TodoListTests
{
    private static string[] Texts(TodoList list) => [.. list.Items.Select(i => i.Done ? "x " + i.Text : i.Text)];

    private static TodoList Of(params string[] texts)
    {
        TodoList list = TodoList.FromJson(null);
        foreach (string text in texts)
        {
            list.Add(text);
        }

        return list;
    }

    [Fact]
    public void Adding_puts_new_items_after_the_open_ones_and_skips_empty_text()
    {
        TodoList list = Of("milk", "eggs");
        list.Toggle(0);
        Assert.True(list.Add("  bread \r\n rolls "));
        Assert.False(list.Add("   "));

        Assert.Equal(["eggs", "bread rolls", "x milk"], Texts(list));
        Assert.Equal(2, list.OpenCount);
    }

    [Fact]
    public void Ticking_moves_down_and_unticking_moves_back_up()
    {
        TodoList list = Of("a", "b", "c");
        list.Toggle(0);
        list.Toggle(0);
        Assert.Equal(["c", "x b", "x a"], Texts(list));

        list.Toggle(2);
        Assert.Equal(["c", "a", "x b"], Texts(list));
    }

    [Fact]
    public void Moves_stay_within_open_or_ticked_items()
    {
        TodoList list = Of("a", "b", "c", "d");
        list.Toggle(3);

        Assert.True(list.Move(2, 0));
        Assert.Equal(["c", "a", "b", "x d"], Texts(list));

        // An open item dragged below the ticked ones stops at the end of the open ones.
        list.Move(0, 99);
        Assert.Equal(["a", "b", "c", "x d"], Texts(list));

        // A ticked one dragged to the top stops at the first ticked place.
        Assert.False(list.Move(3, 0));
        Assert.Equal(["a", "b", "c", "x d"], Texts(list));
    }

    [Fact]
    public void Edit_remove_and_clear_done()
    {
        TodoList list = Of("a", "b", "c");
        list.Edit(0, "apples");
        list.Edit(1, "  ");
        list.Toggle(1);
        Assert.Equal(["apples", "x c"], Texts(list));

        Assert.True(list.AnyDone);
        list.ClearDone();
        Assert.Equal(["apples"], Texts(list));

        list.Remove(0);
        list.Remove(5);
        Assert.Empty(list.Items);
    }

    [Fact]
    public void Round_trips_through_json_and_survives_bad_data()
    {
        TodoList list = Of("a", "b");
        list.Toggle(0);
        TodoList back = TodoList.FromJson(list.ToJson());
        Assert.Equal(Texts(list), Texts(back));

        Assert.Empty(TodoList.FromJson("not json").Items);
        Assert.Equal(["open", "x done"], Texts(TodoList.FromJson("""[{"Text":"done","Done":true},null,{"Text":"","Done":false},{"Text":"open","Done":false}]""")));
    }

    [Fact]
    public void Todo_is_in_the_catalog_and_can_be_added_more_than_once()
    {
        WidgetType type = Assert.Single(WidgetCatalog.All, t => t.Kind == WidgetKinds.Todo);
        Assert.False(type.Single);
        Assert.Contains(HomeLayout.Addable([new HomeWidget { Kind = WidgetKinds.Todo }]), t => t.Kind == WidgetKinds.Todo);
    }
}
