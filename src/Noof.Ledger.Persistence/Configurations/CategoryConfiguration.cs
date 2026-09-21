using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    static readonly Guid Groceries = Guid.Parse("00000000-0000-0000-0001-000000000001");
    static readonly Guid FoodDrink = Guid.Parse("00000000-0000-0000-0001-000000000002");
    static readonly Guid Transport = Guid.Parse("00000000-0000-0000-0001-000000000003");
    static readonly Guid Housing = Guid.Parse("00000000-0000-0000-0001-000000000004");
    static readonly Guid Utilities = Guid.Parse("00000000-0000-0000-0001-000000000005");
    static readonly Guid Health = Guid.Parse("00000000-0000-0000-0001-000000000006");
    static readonly Guid Shopping = Guid.Parse("00000000-0000-0000-0001-000000000007");
    static readonly Guid Entertainment = Guid.Parse("00000000-0000-0000-0001-000000000008");
    static readonly Guid Travel = Guid.Parse("00000000-0000-0000-0001-000000000009");
    static readonly Guid Education = Guid.Parse("00000000-0000-0000-0001-000000000010");
    static readonly Guid Subscriptions = Guid.Parse("00000000-0000-0000-0001-000000000011");
    static readonly Guid GiftsDonations = Guid.Parse("00000000-0000-0000-0001-000000000012");
    static readonly Guid FeesCharges = Guid.Parse("00000000-0000-0000-0001-000000000013");
    static readonly Guid PersonalCare = Guid.Parse("00000000-0000-0000-0001-000000000014");
    static readonly Guid Other = Guid.Parse("00000000-0000-0000-0001-000000000015");
    static readonly Guid Restaurants = Guid.Parse("00000000-0000-0000-0001-000000000016");
    static readonly Guid Coffee = Guid.Parse("00000000-0000-0000-0001-000000000017");
    static readonly Guid Fuel = Guid.Parse("00000000-0000-0000-0001-000000000018");
    static readonly Guid PublicTransport = Guid.Parse("00000000-0000-0000-0001-000000000019");
    static readonly Guid Clothing = Guid.Parse("00000000-0000-0000-0001-000000000020");

    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.ParentId).HasColumnName("parent_id");
        builder.Property(c => c.Slug).HasColumnName("slug").HasMaxLength(64).IsRequired();
        builder.Property(c => c.NameEn).HasColumnName("name_en").HasMaxLength(128).IsRequired();
        builder.Property(c => c.NameRu).HasColumnName("name_ru").HasMaxLength(128).IsRequired();
        builder.Property(c => c.IsActive).HasColumnName("is_active");

        builder.HasIndex(c => c.Slug).IsUnique();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasData(
            Seed(Groceries, null, "groceries", "Groceries", "Продукты"),
            Seed(FoodDrink, null, "food-drink", "Food & Drink", "Еда и напитки"),
            Seed(Transport, null, "transport", "Transport", "Транспорт"),
            Seed(Housing, null, "housing", "Housing", "Жильё"),
            Seed(Utilities, null, "utilities", "Utilities", "Коммунальные услуги"),
            Seed(Health, null, "health", "Health", "Здоровье"),
            Seed(Shopping, null, "shopping", "Shopping", "Покупки"),
            Seed(Entertainment, null, "entertainment", "Entertainment", "Развлечения"),
            Seed(Travel, null, "travel", "Travel", "Путешествия"),
            Seed(Education, null, "education", "Education", "Образование"),
            Seed(Subscriptions, null, "subscriptions", "Subscriptions", "Подписки"),
            Seed(GiftsDonations, null, "gifts-donations", "Gifts & Donations", "Подарки и пожертвования"),
            Seed(FeesCharges, null, "fees-charges", "Fees & Charges", "Комиссии и сборы"),
            Seed(PersonalCare, null, "personal-care", "Personal Care", "Личная гигиена"),
            Seed(Other, null, "other", "Other", "Прочее"),
            Seed(Restaurants, FoodDrink, "restaurants", "Restaurants", "Рестораны"),
            Seed(Coffee, FoodDrink, "coffee", "Coffee", "Кофе"),
            Seed(Fuel, Transport, "fuel", "Fuel", "Топливо"),
            Seed(PublicTransport, Transport, "public-transport", "Public Transport", "Общественный транспорт"),
            Seed(Clothing, Shopping, "clothing", "Clothing", "Одежда"));
    }

    static Category Seed(Guid id, Guid? parentId, string slug, string nameEn, string nameRu) => new()
    {
        Id = id,
        ParentId = parentId,
        Slug = slug,
        NameEn = nameEn,
        NameRu = nameRu,
        IsActive = true,
    };
}
