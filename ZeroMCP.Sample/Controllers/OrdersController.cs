using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SampleApi;
using ZeroMCP.Attributes;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private static List<Order> Store => SampleData.Orders;

    [HttpGet("{id:int}")]
    public ActionResult<Order> GetOrder(int id)
    {
        var order = Store.FirstOrDefault(o => o.Id == id);
        return order is null ? NotFound($"Order {id} not found") : Ok(order);
    }

    [HttpGet("v1/{id:int}")]
    [Mcp("get_order", Version = 1, Description = "Retrieves a single order by ID.")]
    public ActionResult<Order> GetOrderV1(int id)
    {
        var order = Store.FirstOrDefault(o => o.Id == id);
        return order is null ? NotFound($"Order {id} not found") : Ok(order);
    }

    [HttpGet("v2/{id:int}")]
    [Mcp("get_order", Version = 2, Description = "Retrieves a single order by ID with expanded detail.")]
    public ActionResult<OrderDetail> GetOrderV2(int id, [FromQuery] bool includeHistory = false)
    {
        var order = Store.FirstOrDefault(o => o.Id == id);
        if (order is null) return NotFound($"Order {id} not found");
        var detail = new OrderDetail
        {
            Id = order.Id,
            CustomerId = order.CustomerId,
            CustomerName = order.CustomerName,
            Product = order.Product,
            Quantity = order.Quantity,
            Status = order.Status,
            History = includeHistory ? ["created", "pending"] : null
        };
        return Ok(detail);
    }

    [HttpGet("secure/{id:int}")]
    [Authorize]
    [Mcp("get_secure_order", Description = "Retrieves a single order by its numeric ID. Requires authentication.")]
    public ActionResult<Order> GetSecureOrder(int id)
    {
        var order = Store.FirstOrDefault(o => o.Id == id);
        return order is null ? NotFound($"Order {id} not found") : Ok(order);
    }

    [HttpGet]
    [Mcp("list_orders", Description = "Lists all orders. Optionally filter by status (pending, shipped, cancelled).")]
    public ActionResult<List<Order>> ListOrders([FromQuery] string? status = null)
    {
        var orders = status is null
            ? Store.ToList()
            : Store.Where(o => o.Status.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
        return Ok(orders);
    }

    [HttpPost]
    [Mcp("create_order", Description = "Creates a new order. Returns the created order with its assigned ID.", Tags = ["write"])]
    public ActionResult<Order> CreateOrder([FromBody] CreateOrderRequest request)
    {
        var order = new Order
        {
            Id = Store.Count + 1,
            CustomerName = request.CustomerName,
            Product = request.Product,
            Quantity = request.Quantity,
            Status = "pending"
        };
        Store.Add(order);
        return Created($"/api/orders/{order.Id}", order);
    }

    [HttpPost("upload")]
    [Mcp("upload_document", Description = "Uploads a document. Accepts base64-encoded content via MCP.", Tags = ["write"])]
    public ActionResult<object> UploadDocument(IFormFile document, [FromForm] string? title = null)
    {
        if (document == null)
            return BadRequest(new { error = "Document is required" });
        if (document.Length == 0)
            return BadRequest(new { error = "Empty file" });
        return Ok(new { filename = document.FileName, size = document.Length, contentType = document.ContentType ?? "application/octet-stream", title = title ?? "untitled" });
    }

    [HttpPatch("{id:int}/status")]
    [Mcp("update_order_status", Description = "Updates the status of an existing order.")]
    public ActionResult<Order> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        var order = Store.FirstOrDefault(o => o.Id == id);
        if (order is null) return NotFound($"Order {id} not found");
        order.Status = request.Status;
        return Ok(order);
    }

    [HttpDelete("{id:int}")]
    public IActionResult DeleteOrder(int id)
    {
        var order = Store.FirstOrDefault(o => o.Id == id);
        if (order is null) return NotFound();
        Store.Remove(order);
        return NoContent();
    }

    /// <summary>
    /// Streams all orders one at a time with a simulated delay. Demonstrates IAsyncEnumerable streaming over MCP.
    /// </summary>
    [HttpGet("stream")]
    [Mcp("stream_orders", Description = "Streams all orders progressively with a simulated delay between each item.")]
    public async IAsyncEnumerable<Order> StreamOrders([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Snapshot: Store is shared and mutable (create_order), and this enumeration spans seconds.
        foreach (var order in Store.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken);
            yield return order;
        }
    }
}

public class Order
{
    public int Id { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; } = default!;
    public string Product { get; set; } = default!;
    public int Quantity { get; set; }
    public string Status { get; set; } = "pending";
}

public class OrderDetail
{
    public int Id { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; } = default!;
    public string Product { get; set; } = default!;
    public int Quantity { get; set; }
    public string Status { get; set; } = "pending";
    public string[]? History { get; set; }
}

public class CreateOrderRequest
{
    [Required][MinLength(1)]
    public string CustomerName { get; set; } = default!;
    [Required]
    public string Product { get; set; } = default!;
    [Range(1, 1000)]
    public int Quantity { get; set; } = 1;
}

public class UpdateStatusRequest
{
    [Required]
    [RegularExpression("^(pending|shipped|cancelled)$", ErrorMessage = "Status must be one of: pending, shipped, cancelled.")]
    public string Status { get; set; } = default!;
}
