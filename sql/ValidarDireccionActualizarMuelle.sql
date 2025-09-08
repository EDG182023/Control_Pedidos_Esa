CREATE OR ALTER PROCEDURE ValidarDireccionActualizarMuelle
    @Direccion      NVARCHAR(255),
    @CodigoPostal   NVARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AreaMuelle INT;

    -- Ejemplo de determinación de área de muelle según el código postal
    SELECT TOP 1 @AreaMuelle = AreaMuelle
    FROM   MuelleAreas
    WHERE  CodigoPostal = @CodigoPostal;

    IF @AreaMuelle IS NULL
    BEGIN
        THROW 50001, 'No se encontró un área de muelle para el código postal especificado.', 1;
    END

    -- Actualizar la tabla de cabeceras con el área de muelle calculada
    UPDATE Cabeceras
    SET    AreaMuelle = @AreaMuelle
    WHERE  Direccion = @Direccion
           AND CodigoPostal = @CodigoPostal;
END
GO
