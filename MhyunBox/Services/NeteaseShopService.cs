using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MhyunBox.Services;

public sealed class ShopCategory
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public int ItemCount { get; set; }
}

public sealed class ShopItem
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Summary { get; set; } = "";

	public string Description { get; set; } = "";

	public string ImageUrl { get; set; } = "";

	public int OriginalDiamonds { get; set; }

	public int FinalDiamonds { get; set; }

	public int OriginalPoints { get; set; }

	public int FinalPoints { get; set; }
}

public sealed class ShopQuote
{
	public string ItemId { get; set; } = "";

	public string Name { get; set; } = "";

	public string Summary { get; set; } = "";

	public string ImageUrl { get; set; } = "";

	public int OriginalDiamonds { get; set; }

	public int FinalDiamonds { get; set; }

	public int OriginalPoints { get; set; }

	public int FinalPoints { get; set; }
}

public sealed class ShopCurrency
{
	public int PaidDiamonds { get; set; }

	public int FreeDiamonds { get; set; }

	public int TotalDiamonds { get; set; }
}

public sealed class ShopPageResult
{
	public ShopItem[] Items { get; set; } = Array.Empty<ShopItem>();

	public int Total { get; set; }
}

public sealed class ShopPurchaseResult
{
	public bool Success { get; set; }

	public string OrderId { get; set; } = "";

	public string Message { get; set; } = "";
}

public static class NeteaseShopService
{
	private const string ApiBase = "https://x19apigatewayobt.nie.netease.com";

	private const string UserAgent = "WPFLauncher/0.0.0.0";

	private const string CategoryPath = "/shop-item-type/query/search-by-gc-item";

	private const string ItemPath = "/shop-item/query/search-by-gc-item-type";

	private const string CurrencyPath = "/user-currency/query";

	private const string PricePath = "/mc-item-details/get-info";

	private const string BuyPath = "/mc-item-buy";

	private const string BuyResultPath = "/buy-item-result";

	private static readonly JsonSerializerOptions Relaxed = new JsonSerializerOptions
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public static async Task<ShopCategory[]> GetCategoriesAsync(string entityId, string accessToken, string serverId, CancellationToken ct = default(CancellationToken))
	{
		JsonElement root = await PostAsync(CategoryPath, new
		{
			gc_item_id = serverId,
			offset = 0,
			length = 100
		}, entityId, accessToken, ct);
		List<ShopCategory> list = new List<ShopCategory>();
		foreach (JsonElement item in EnumerateArray(root, "entities"))
		{
			string id = GetString(item, "entity_id");
			if (!string.IsNullOrWhiteSpace(id))
			{
				list.Add(new ShopCategory
				{
					Id = id,
					Name = GetString(item, "name"),
					ItemCount = GetInt(item, "mc_item_count")
				});
			}
		}
		LogRawResult($"分类 server={serverId}", root, list.Count);
		return list.ToArray();
	}

	public static async Task<ShopPageResult> GetItemsAsync(string entityId, string accessToken, string serverId, string categoryId, int offset, int length, CancellationToken ct = default(CancellationToken))
	{
		JsonElement root = await PostAsync(ItemPath, new
		{
			gc_item_id = serverId,
			shop_item_type_id = categoryId,
			offset = Math.Max(0, offset),
			length = Math.Clamp(length, 1, 100)
		}, entityId, accessToken, ct);
		List<ShopItem> list = new List<ShopItem>();
		foreach (JsonElement item in EnumerateArray(root, "entities"))
		{
			string id = GetString(item, "entity_id");
			if (string.IsNullOrWhiteSpace(id))
			{
				continue;
			}
			list.Add(new ShopItem
			{
				Id = id,
				Name = GetString(item, "name"),
				Summary = GetString(item, "brief_summary"),
				Description = GetString(item, "info"),
				ImageUrl = GetString(item, "image_url"),
				OriginalDiamonds = GetInt(item, "original_diamonds"),
				FinalDiamonds = GetInt(item, "final_diamonds"),
				OriginalPoints = GetInt(item, "original_points"),
				FinalPoints = GetInt(item, "final_points")
			});
		}
		LogRawResult($"商品 server={serverId} cat={categoryId}", root, list.Count);
		return new ShopPageResult
		{
			Items = list.ToArray(),
			Total = GetInt(root, "total", list.Count)
		};
	}

	private static void LogRawResult(string label, JsonElement root, int count)
	{
		int code = GetInt(root, "code", -1);
		if (count > 0)
		{
			LogHelper.Log($"[商城] {label} => code={code} 数量={count}");
			return;
		}
		string raw = root.GetRawText();
		if (raw.Length > 800)
		{
			raw = raw.Substring(0, 800);
		}
		LogHelper.Log($"[商城] {label} => code={code} 空结果，原始响应：{raw}");
	}

	public static async Task<ShopCurrency> GetCurrencyAsync(string entityId, string accessToken, CancellationToken ct = default(CancellationToken))
	{
		JsonElement root = await PostAsync(CurrencyPath, null, entityId, accessToken, ct);
		JsonElement entity = GetObject(root, "entity");
		int paid = GetInt(entity, "pay_diamond");
		int free = GetInt(entity, "free_diamond");
		return new ShopCurrency
		{
			PaidDiamonds = paid,
			FreeDiamonds = free,
			TotalDiamonds = paid + free
		};
	}

	public static async Task<ShopQuote> GetQuoteAsync(string entityId, string accessToken, string itemId, CancellationToken ct = default(CancellationToken))
	{
		JsonElement root = await PostAsync(PricePath, new
		{
			item_id = itemId
		}, entityId, accessToken, ct);
		JsonElement entity = GetObject(root, "entity");
		return new ShopQuote
		{
			ItemId = itemId,
			Name = GetString(entity, "name"),
			Summary = GetString(entity, "breif"),
			ImageUrl = GetString(entity, "brief_image_url"),
			OriginalDiamonds = GetInt(entity, "original_diamonds"),
			FinalDiamonds = GetInt(entity, "final_diamonds"),
			OriginalPoints = GetInt(entity, "original_points"),
			FinalPoints = GetInt(entity, "final_points")
		};
	}

	public static async Task<ShopPurchaseResult> PurchaseAsync(string entityId, string accessToken, string itemId, int expectedDiamonds, int expectedPoints, CancellationToken ct = default(CancellationToken))
	{
		ShopQuote shopQuote = await GetQuoteAsync(entityId, accessToken, itemId, ct);
		if (shopQuote.FinalDiamonds != expectedDiamonds || shopQuote.FinalPoints != expectedPoints)
		{
			throw new InvalidOperationException("商品价格已发生变化，请重新确认后购买。");
		}
		JsonElement root = await PostAsync(BuyPath, new
		{
			entity_id = 0,
			item_id = itemId,
			item_num = 1,
			shop_type = 1,
			receiver_id = string.Empty
		}, entityId, accessToken, ct);
		JsonElement entity = GetObject(root, "entity");
		string orderId = GetString(entity, "entity_id");
		int buyType = GetInt(entity, "buy_type");
		if (string.IsNullOrWhiteSpace(orderId))
		{
			throw new InvalidOperationException("服务器未返回有效订单号。");
		}
		for (int attempt = 0; attempt < 5; attempt++)
		{
			if (attempt > 0)
			{
				await Task.Delay(TimeSpan.FromSeconds(2L), ct);
			}
			JsonElement result = await PostRawAsync(BuyResultPath, new
			{
				orderid = orderId,
				buy_type = buyType
			}, entityId, accessToken, ct);
			if (GetInt(result, "code", -1) == 0)
			{
				return new ShopPurchaseResult
				{
					Success = true,
					OrderId = orderId,
					Message = GetString(result, "message", "购买成功")
				};
			}
		}
		return new ShopPurchaseResult
		{
			Success = false,
			OrderId = orderId,
			Message = "订单已提交，但暂未确认到账，请稍后在官方账户中核对。"
		};
	}

	private static async Task<JsonElement> PostAsync(string path, object payload, string entityId, string accessToken, CancellationToken ct)
	{
		JsonElement root = await PostRawAsync(path, payload, entityId, accessToken, ct);
		int code = GetInt(root, "code", -1);
		if (code != 0)
		{
			string message = GetString(root, "message");
			string full = string.IsNullOrWhiteSpace(message) ? $"接口错误码 {code}" : $"{message} ({code})";
			if (ElsewhereLoginNotice.IsElsewhereMessage(message))
			{
				AccountStore.LocalAccount localAccount = AccountStore.All.FirstOrDefault((AccountStore.LocalAccount a) => string.Equals(a.EntityId, entityId, StringComparison.OrdinalIgnoreCase));
				if (localAccount != null)
				{
					ElsewhereLoginNotice.Mark(localAccount.Channel, localAccount.Account, "商城接口提示疑似别处登录：" + full);
				}
			}
			throw new InvalidOperationException(full);
		}
		return root;
	}

	private static async Task<JsonElement> PostRawAsync(string path, object payload, string entityId, string accessToken, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(entityId) || string.IsNullOrWhiteSpace(accessToken))
		{
			throw new InvalidOperationException("账号登录状态无效，请先在账号绑定页登录。");
		}
		string body = ((payload == null) ? string.Empty : JsonSerializer.Serialize(payload, payload.GetType(), Relaxed));
		using HttpClient http = new HttpClient();
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, ApiBase + path);
		request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
		request.Content = new StringContent(body, Encoding.UTF8, "application/json");
		foreach (KeyValuePair<string, string> header in NeteaseCipher.ComputeHttpRequestToken(path, body, entityId, accessToken))
		{
			request.Headers.TryAddWithoutValidation(header.Key, header.Value);
		}
		using HttpResponseMessage response = await http.SendAsync(request, ct);
		string text = await response.Content.ReadAsStringAsync(ct);
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException($"网易接口请求失败：HTTP {(int)response.StatusCode}");
		}
		try
		{
			using JsonDocument document = JsonDocument.Parse(text);
			return document.RootElement.Clone();
		}
		catch (JsonException inner)
		{
			LogHelper.LogError(inner, "[商城] 网易接口返回无法解析的数据");
			throw new InvalidOperationException("网易接口返回了无法解析的数据。", inner);
		}
	}

	private static IEnumerable<JsonElement> EnumerateArray(JsonElement element, string name)
	{
		if (TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in value.EnumerateArray())
			{
				yield return item;
			}
		}
	}

	private static JsonElement GetObject(JsonElement element, string name)
	{
		if (TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.Object)
		{
			return value;
		}
		throw new InvalidOperationException($"网易接口返回缺少字段：{name}");
	}

	private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
				{
					value = property.Value;
					return true;
				}
			}
		}
		value = default(JsonElement);
		return false;
	}

	private static string GetString(JsonElement element, string name, string fallback = "")
	{
		if (!TryGetProperty(element, name, out JsonElement value))
		{
			return fallback;
		}
		return value.ValueKind switch
		{
			JsonValueKind.String => value.GetString() ?? fallback,
			JsonValueKind.Number => value.GetRawText(),
			_ => fallback
		};
	}

	private static int GetInt(JsonElement element, string name, int fallback = 0)
	{
		if (!TryGetProperty(element, name, out JsonElement value))
		{
			return fallback;
		}
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
		{
			return number;
		}
		if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
		{
			return number;
		}
		return fallback;
	}
}
